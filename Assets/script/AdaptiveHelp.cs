using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

// AIDE ADAPTATIVE (partie Machine Learning) : Knowledge Tracing bayésien (BKT).
//
// Pour chaque compétence (poser un piston, poser le chapeau, serrer au couple, respecter l'ordre de serrage...),
// le modèle estime la probabilité que l'apprenant la MAÎTRISE, et la met à jour après chaque tentative :
//   réussite -> la probabilité monte ; erreur -> elle baisse ; à chaque occasion, une petite chance d'apprendre.
// Le niveau d'aide de chaque étape en découle :
//   niveau 2 (débutant, maîtrise < Low)    : consigne + conseil + pièce colorée ; le conseil revient après une erreur
//   niveau 1 (en cours)                    : consigne + pièce colorée
//   niveau 0 (maîtrisé, maîtrise >= High)  : consigne courte, pièce non colorée (seul le fantôme reste)
// La maîtrise est MÉMORISÉE par participant : au 2e essai, l'aide est déjà réduite là où il a réussi.
//
// Les 4 paramètres de chaque compétence (p_init, p_learn, p_slip, p_guess) sont estimés à partir des sessions
// enregistrées par le script analyse_sessions.py (fichier bkt_params.json), à glisser dans « Parameters ».
//
// À placer sur un objet de la scène (ex. Tutoriel). Il trouve tout seul les pièces et les vis.

[DefaultExecutionOrder(-40)]   // s'abonne avant la séquence : la maîtrise est à jour quand l'étape suivante s'affiche
public class AdaptiveHelp : MonoBehaviour
{
    public static AdaptiveHelp Instance { get; private set; }

    [Header("Mode")]
    [Tooltip("Décoché : aide complète pour tout le monde (comme avant). Utile pour comparer avec / sans adaptation.")]
    public bool adaptive = true;

    [Header("Couleur des pièces à prendre")]
    [Tooltip("Coché : la couleur orange reste sur les pièces à prendre, même quand la compétence est maîtrisée.")]
    public bool keepHighlight = true;

    [Header("Seuils de maîtrise")]
    [Tooltip("En dessous : niveau d'aide 2 (débutant).")]
    [Range(0f, 1f)] public float low = 0.5f;
    [Tooltip("Au-dessus : compétence maîtrisée, aide minimale.")]
    [Range(0f, 1f)] public float high = 0.85f;

    [Header("Paramètres du modèle")]
    [Tooltip("Optionnel : bkt_params.json produit par analyse_sessions.py. Vide = valeurs par défaut.")]
    public TextAsset parameters;
    [Tooltip("Valeurs par défaut (avant la collecte de données).")]
    public float defaultInit = 0.3f, defaultLearn = 0.2f, defaultSlip = 0.1f, defaultGuess = 0.25f;

    [Header("Mémoire")]
    [Tooltip("Coché : la maîtrise de chaque participant est conservée d'une session à l'autre (pas pour P00).")]
    public bool rememberParticipant = true;

    public enum Level { Maitrise = 0, EnCours = 1, Debutant = 2 }

    private class Bkt { public float init, learn, slip, guess; }

    private readonly Dictionary<string, Bkt> prm = new Dictionary<string, Bkt>();
    private readonly Dictionary<string, float> mastery = new Dictionary<string, float>();
    private readonly Dictionary<NutTightening, int> overFrame = new Dictionary<NutTightening, int>();
    private string participant = "P00";
    private SessionLogger logger;

    public static readonly string[] Skills =
    {
        "piston", "bielle", "axe", "bague", "vilebrequin", "chapeau", "vis_chc", "ecrou",
        "ensemble", "carter", "vis_carter", "serrage", "ordre_serrage", "ordre_montage"
    };

    // ---------- Compétence d'une pièce (mêmes règles que analyse_sessions.py) ----------

    public static string SkillOf(string name)
    {
        string s = (name ?? "").ToLowerInvariant();
        if (s.Contains("bushing") || s.Contains("bague")) return "bague";
        if (s.StartsWith("ass_pist")) return "ensemble";
        if (s.Contains("crank")) return "vilebrequin";
        if (s.Contains("cap_rod") || s.Contains("cap rod")) return "chapeau";
        if (s.Contains("piston_pin")) return "axe";
        if (s.Contains("piston_rod")) return "bielle";
        if (s.Contains("piston_head")) return "piston";
        if (s.Contains("socket head")) return "vis_chc";
        if (s.Contains("vis") || s.Contains("screw") || s.Contains("bolt")) return "vis_carter";
        if (s.Contains("nut")) return "ecrou";
        if (s.Contains("carter") || s.Contains("oil_pan")) return "carter";
        return "";
    }

    // ---------- Démarrage ----------

    void Awake()
    {
        Instance = this;
        SessionLogger.ParticipantLocked += OnParticipantLocked;
        foreach (string k in Skills) prm[k] = new Bkt { init = defaultInit, learn = defaultLearn, slip = defaultSlip, guess = defaultGuess };
        if (parameters != null) LoadParameters(parameters.text);
        foreach (string k in Skills) mastery[k] = prm[k].init;

        // Tentatives de pose.
        foreach (var piece in FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            SnapToTarget p = piece;
            p.onSnapped.AddListener(() => Observe(SkillOf(p.name), true, p.name));
            p.onWrongOrientation.AddListener(() => Observe(SkillOf(p.name), false, p.name));
            p.onWrongSide.AddListener(() => Observe(SkillOf(p.name), false, p.name));
            // Ordre de montage (graphe de précédence) : une erreur d'ordre, ou une pose dans le bon ordre.
            p.onWrongOrder.AddListener(() => Observe("ordre_montage", false, p.name));
            p.onSnapped.AddListener(() => { if (GhostSequence.Instance != null && GhostSequence.Instance.FreeOrder) Observe("ordre_montage", true, p.name); });
            // Relâchée loin du fantôme : simple reprise en main, pas une tentative (même règle que le journal).
            p.onWrongPlace.AddListener(() => { if (p.LastDistance <= 0.10f) Observe(SkillOf(p.name), false, p.name); });
            // Mauvaise pièce : c'est la compétence de l'étape en cours qui est en défaut.
            p.onWrongPiece.AddListener(() =>
            {
                GhostSequence seq = FindFirstObjectByType<GhostSequence>();
                string skill = seq != null && seq.Current != null ? SkillOf(seq.Current.name) : SkillOf(p.name);
                Observe(skill, false, p.name);
            });
        }

        // Serrage.
        foreach (var fastener in FindObjectsByType<NutTightening>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            NutTightening f = fastener;
            f.onOverTightened.AddListener(() => { overFrame[f] = Time.frameCount; Observe("serrage", false, f.name); });
            f.onUnderTightened.AddListener(() => Observe("serrage", false, f.name));
            f.onOutOfSequence.AddListener(() => Observe("ordre_serrage", false, f.name));
            f.onTorqueValidated.AddListener(() =>
            {
                // Une validation juste après un sur-serrage n'est pas une réussite.
                bool justOver = overFrame.TryGetValue(f, out int frame) && frame == Time.frameCount;
                if (!justOver) Observe("serrage", true, f.name);
                if (f.group != null && f.group.crossPattern) Observe("ordre_serrage", true, f.name);
            });
        }
    }

    void Start()
    {
        if (logger == null) logger = FindFirstObjectByType<SessionLogger>(FindObjectsInactive.Include);
        Debug.Log("[Guide] aide adaptative " + (adaptive ? "ACTIVE" : "désactivée (aide complète)")
                  + ", paramètres " + (parameters != null ? "issus des données (" + parameters.name + ")" : "par défaut") + ".");
    }

    void OnDestroy()
    {
        SessionLogger.ParticipantLocked -= OnParticipantLocked;
        if (Instance == this) Instance = null;
    }

    // Le participant est connu (premier appui sur A) : on reprend sa maîtrise mémorisée.
    private void OnParticipantLocked(string code)
    {
        participant = code;
        if (logger == null) logger = FindFirstObjectByType<SessionLogger>(FindObjectsInactive.Include);
        int restored = 0;
        if (rememberParticipant && participant != "P00")
        {
            foreach (string k in Skills)
            {
                string key = PrefKey(k);
                if (PlayerPrefs.HasKey(key)) { mastery[k] = PlayerPrefs.GetFloat(key); restored++; }
            }
        }
        Debug.Log("[Guide] maîtrise de " + participant + (restored > 0 ? " reprise de ses essais précédents." : " : premier essai."));
        if (logger != null)
            foreach (string k in Skills) logger.LogEvent("maitrise_depart", k, F(mastery[k]), adaptive ? "1" : "0");
        GhostSequence seq = FindFirstObjectByType<GhostSequence>();
        if (seq != null) seq.Refresh();
    }

    // ---------- Modèle BKT ----------

    private void Observe(string skill, bool correct, string obj)
    {
        if (string.IsNullOrEmpty(skill) || !prm.ContainsKey(skill)) return;
        Bkt b = prm[skill];
        float pl = mastery[skill];
        float pOk = pl * (1f - b.slip) + (1f - pl) * b.guess;
        float post = correct ? pl * (1f - b.slip) / Mathf.Max(pOk, 1e-6f)
                             : pl * b.slip / Mathf.Max(1f - pOk, 1e-6f);
        float next = post + (1f - post) * b.learn;
        mastery[skill] = Mathf.Clamp01(next);

        if (rememberParticipant && participant != "P00")
        {
            PlayerPrefs.SetFloat(PrefKey(skill), mastery[skill]);
            PlayerPrefs.Save();
        }
        Debug.Log("[Guide] maîtrise " + skill + " : " + F(pl) + " -> " + F(mastery[skill]) + " (aide niveau " + (int)LevelOf(skill) + ").");
        if (logger != null) logger.LogEvent("maitrise", skill, F(mastery[skill]), ((int)LevelOf(skill)).ToString());
    }

    // ---------- Ce que le tutoriel et la séquence demandent ----------

    public float Mastery(string skill) => mastery.TryGetValue(skill ?? "", out float m) ? m : 0f;

    public Level LevelOf(string skill)
    {
        if (!adaptive) return Level.Debutant;
        float m = Mastery(skill);
        if (m >= high) return Level.Maitrise;
        if (m >= low) return Level.EnCours;
        return Level.Debutant;
    }

    public Level LevelFor(SnapToTarget piece) => piece == null ? Level.Debutant : LevelOf(SkillOf(piece.name));

    // ANTICIPATION : probabilité que la PROCHAINE tentative sur cette compétence soit une erreur,
    // d'après le modèle : P(erreur) = 1 - [ P(maîtrise) x (1 - p_slip) + (1 - P(maîtrise)) x p_guess ].
    public float PredictError(string skill)
    {
        if (string.IsNullOrEmpty(skill) || !prm.ContainsKey(skill)) return 0f;
        Bkt b = prm[skill];
        float pl = Mastery(skill);
        return 1f - (pl * (1f - b.slip) + (1f - pl) * b.guess);
    }

    // La pièce à prendre est colorée tant que la compétence n'est pas maîtrisée.
    // La couleur orange est-elle gardée pour cette pièce ?
    // « Keep Highlight » coché : toujours (la couleur indique quelles pièces prendre, ce n'est pas une aide à retirer) ;
    // décoché : seulement tant que la compétence n'est pas maîtrisée.
    public bool ShouldHighlight(SnapToTarget piece) => keepHighlight || LevelFor(piece) != Level.Maitrise;

    // Le conseil de la pièce est affiché pour un débutant.
    public bool ShouldGiveAdvice(SnapToTarget piece) => LevelFor(piece) == Level.Debutant;

    // ---------- Paramètres ----------

    // Lit bkt_params.json : { "piston": { "p_init": 0.1, "p_learn": 0.05, ... }, ... }
    private void LoadParameters(string json)
    {
        int loaded = 0;
        foreach (string k in Skills)
        {
            Match block = Regex.Match(json, "\"" + k + "\"\\s*:\\s*\\{([^}]*)\\}");
            if (!block.Success) continue;
            string body = block.Groups[1].Value;
            Bkt b = prm[k];
            b.init = Read(body, "p_init", b.init);
            b.learn = Read(body, "p_learn", b.learn);
            b.slip = Read(body, "p_slip", b.slip);
            b.guess = Read(body, "p_guess", b.guess);
            loaded++;
        }
        Debug.Log("[Guide] paramètres BKT chargés pour " + loaded + " compétence(s).");
    }

    private static float Read(string body, string key, float fallback)
    {
        Match m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*([-0-9.eE]+)");
        return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? Mathf.Clamp(v, 0.001f, 0.999f) : fallback;
    }

    private string PrefKey(string skill) => "MoteurV6_bkt_" + participant + "_" + skill;
    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
