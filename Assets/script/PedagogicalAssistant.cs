using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// ASSISTANT PÉDAGOGIQUE : conseils et consignes automatiques PENDANT les manipulations.
//
// 1. Retour sur erreur : chaque erreur détectée (orientation, emplacement, mauvaise pièce, sens du moteur,
//    mauvais outil, sur-serrage, couple insuffisant, ordre de serrage) reçoit un retour en trois parties :
//      QUOI (ce qui ne va pas)  -  POURQUOI (la conséquence sur un vrai moteur)  -  COMMENT (corriger).
//    Le niveau de détail suit l'aide adaptative : débutant = les trois ; en cours = quoi + comment ; maîtrisé = quoi.
//    Une même erreur répétée deux fois de suite déclenche toujours le retour complet.
// 2. Guidage en direct : pendant qu'on tient la pièce près de son fantôme, la rotation qui manque est calculée
//    et dite avec des mots, par rapport au regard de l'utilisateur (« tournez-la d'un quart de tour vers la droite »).
// 3. Anticipation : quand on attrape une pièce dont la compétence est peu maîtrisée, le modèle (AdaptiveHelp)
//    estime le risque d'erreur ; au-delà d'un seuil, le conseil est donné AVANT l'erreur.
// 4. Hésitation : pièce tenue longtemps sans être posée, ou aucune action pendant un moment : conseil ciblé.
//
// Les textes sont dans un catalogue modifiable SANS CODE (fichier CSV, champ « Catalogue ») :
//   competence;erreur;quoi;pourquoi;comment      (competence = * pour toutes les pièces)
// Pour un autre produit à assembler, il suffit de réécrire ce fichier.
//
// À placer sur un objet de la scène (ex. Tutoriel), avec TutorialGuide et AdaptiveHelp.

public class PedagogicalAssistant : MonoBehaviour
{
    [Header("Catalogue des retours (optionnel)")]
    [Tooltip("Fichier CSV « competence;erreur;quoi;pourquoi;comment ». Vide = textes intégrés (moteur V6).")]
    public TextAsset catalogue;

    [Header("Retour sur erreur")]
    public bool errorFeedback = true;
    public float feedbackDuration = 9f;

    [Header("Guidage en direct")]
    public bool liveCoaching = true;
    [Tooltip("Distance (m) au fantôme à partir de laquelle le guidage en mots s'affiche.")]
    public float coachingDistance = 0.15f;

    [Header("Anticipation (modèle BKT)")]
    public bool anticipation = true;
    [Tooltip("Risque d'erreur prédit au-delà duquel le conseil est donné avant l'erreur.")]
    [Range(0f, 1f)] public float riskThreshold = 0.45f;

    [Header("Hésitation")]
    public bool hesitationHelp = true;
    [Tooltip("Pièce tenue plus longtemps que cela (s) sans être posée.")]
    public float holdTimeout = 15f;
    [Tooltip("Aucune action pendant cette durée (s) au cours d'une étape.")]
    public float idleTimeout = 25f;

    private class Entry { public string what, why, how; }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

    private TutorialGuide tutorial;
    private GhostSequence sequence;
    private SessionLogger logger;
    private Transform head;
    private SnapToTarget[] pieces = new SnapToTarget[0];

    private string lastErrorKey = "";
    private int repeatCount;
    private float lastActivity;
    private float grabTime;
    private SnapToTarget held;
    private bool idleShown, holdShown;
    private float nextCoachTime;
    private string lastCoachText = "";
    private readonly HashSet<SnapToTarget> anticipatedThisStep = new HashSet<SnapToTarget>();

    // ---------- Démarrage ----------

    void Awake()
    {
        LoadDefaults();
        if (catalogue != null) LoadCatalogue(catalogue.text);
    }

    void Start()
    {
        tutorial = FindFirstObjectByType<TutorialGuide>(FindObjectsInactive.Include);
        sequence = FindFirstObjectByType<GhostSequence>(FindObjectsInactive.Include);
        logger = FindFirstObjectByType<SessionLogger>(FindObjectsInactive.Include);
        if (Camera.main != null) head = Camera.main.transform;
        lastActivity = Time.time;

        pieces = FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var piece in pieces)
        {
            SnapToTarget p = piece;
            p.onGrab.AddListener(() => OnGrab(p));
            p.onSnapped.AddListener(() => { Activity(); held = null; lastErrorKey = ""; });
            p.onWrongOrientation.AddListener(() => Error(p, "orientation"));
            p.onWrongPlace.AddListener(() => { if (p.LastDistance <= 0.10f) Error(p, "endroit"); else held = null; });
            p.onWrongPiece.AddListener(() => Error(p, "mauvaise_piece"));
            p.onWrongSide.AddListener(() => Error(p, "sens_moteur"));
            p.onWrongOrder.AddListener(() => OrderError(p));
        }

        foreach (var tool in FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Tool t = tool;
            t.onGrabbed.AddListener(Activity);
            t.onEngaged.AddListener(Activity);
            t.onWrongTool.AddListener(() => ErrorSkill("*", "mauvais_outil", t.name));
        }

        foreach (var fastener in FindObjectsByType<NutTightening>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            NutTightening f = fastener;
            f.onTorqueValidated.AddListener(Activity);
            f.onOverTightened.AddListener(() => ErrorSkill("serrage", "sur_serrage", f.name));
            f.onUnderTightened.AddListener(() => ErrorSkill("serrage", "couple_insuffisant", f.name));
            f.onOutOfSequence.AddListener(() =>
                ErrorSkill("ordre_serrage", f.group != null && f.group.crossPattern ? "hors_sequence_croix" : "hors_sequence", f.name));
        }

        if (sequence != null) sequence.StepChanged += (piece, i, n) => { anticipatedThisStep.Clear(); Activity(); };

        Debug.Log("[Guide] assistant pédagogique prêt : " + entries.Count + " retours dans le catalogue"
                  + (catalogue != null ? " (" + catalogue.name + ")." : " (textes intégrés)."));
    }

    private void Activity()
    {
        lastActivity = Time.time;
        idleShown = false;
    }

    // ---------- 3. Anticipation ----------

    private void OnGrab(SnapToTarget p)
    {
        Activity();
        held = p;
        grabTime = Time.time;
        holdShown = false;

        if (!anticipation || anticipatedThisStep.Contains(p)) return;

        // Pièce qui ne peut pas encore être posée : on prévient AVANT l'erreur d'ordre.
        if (sequence != null && !p.IsPlaced && sequence.IsInGamme(p) && !sequence.IsAvailable(p))
        {
            anticipatedThisStep.Add(p);
            string missing = sequence.MissingText(p);
            Show((sequence.FreeOrder ? "Pas encore : cette pièce vient plus tard." : "Ce n'est pas la pièce de cette étape.")
                 + (missing != "" ? "\nD'abord : " + missing + "." : "")
                 + "\nLes pièces possibles sont colorées en orange.", feedbackDuration);
            Record("anticipe", AdaptiveHelp.SkillOf(p.name), "pas_disponible");
            return;
        }

        AdaptiveHelp ah = AdaptiveHelp.Instance;
        if (ah == null || !ah.adaptive) return;
        string skill = AdaptiveHelp.SkillOf(p.name);
        float risk = ah.PredictError(skill);
        if (risk < riskThreshold) return;

        anticipatedThisStep.Add(p);
        Entry e = Find(skill, "conseil");
        if (e == null) return;
        Show("Attention : " + e.what + (string.IsNullOrEmpty(e.how) ? "" : "\n" + e.how), feedbackDuration);
        Record("anticipe", skill, risk.ToString("F2", CultureInfo.InvariantCulture));
    }

    // ---------- 1. Retour sur erreur ----------

    private void Error(SnapToTarget p, string error)
    {
        Activity();
        string skill = AdaptiveHelp.SkillOf(p.name);
        if (error == "mauvaise_piece" && sequence != null && sequence.Current != null)
            skill = AdaptiveHelp.SkillOf(sequence.Current.name);
        ErrorSkill(skill, error, p.name);
        held = null;
    }

    // Erreur d'ordre : le retour cite ce qu'il fallait faire avant.
    private void OrderError(SnapToTarget p)
    {
        Activity();
        held = null;
        if (!errorFeedback) return;
        string key = "ordre_montage/" + p.name;
        repeatCount = key == lastErrorKey ? repeatCount + 1 : 1;
        lastErrorKey = key;

        Entry e = Find("ordre_montage", "ordre") ?? Find("*", "ordre");
        string missing = sequence != null ? sequence.MissingText(p) : "";
        bool blocked = sequence == null || sequence.orderErrorPolicy == GhostSequence.OrderErrorPolicy.Bloquer || !sequence.FreeOrder;
        AdaptiveHelp ah = AdaptiveHelp.Instance;
        AdaptiveHelp.Level level = ah != null ? ah.LevelOf("ordre_montage") : AdaptiveHelp.Level.Debutant;
        if (repeatCount >= 2) level = AdaptiveHelp.Level.Debutant;

        string text = (e != null ? e.what : "Trop tôt pour cette pièce.")
                      + (blocked ? "" : " Elle est posée, mais hors gamme.");
        if (level == AdaptiveHelp.Level.Debutant && e != null && !string.IsNullOrEmpty(e.why)) text += "\nPourquoi : " + e.why;
        if (missing != "") text += "\nD'abord : " + missing + ".";
        Show(text, feedbackDuration);
        Record("erreur", "ordre_montage:ordre", ((int)level).ToString() + (repeatCount >= 2 ? "_repetee" : ""));
    }

    private void ErrorSkill(string skill, string error, string obj)
    {
        if (!errorFeedback) return;
        Activity();

        string key = skill + "/" + error;
        repeatCount = key == lastErrorKey ? repeatCount + 1 : 1;
        lastErrorKey = key;

        Entry e = Find(skill, error);
        if (e == null) return;

        // Niveau de détail : suit la maîtrise ; une erreur répétée reçoit toujours le retour complet.
        AdaptiveHelp ah = AdaptiveHelp.Instance;
        AdaptiveHelp.Level level = ah != null ? ah.LevelOf(skill == "*" ? "" : skill) : AdaptiveHelp.Level.Debutant;
        if (skill == "*" || repeatCount >= 2) level = AdaptiveHelp.Level.Debutant;

        string text = e.what;
        if (level == AdaptiveHelp.Level.Debutant && !string.IsNullOrEmpty(e.why)) text += "\nPourquoi : " + e.why;
        if (level != AdaptiveHelp.Level.Maitrise && !string.IsNullOrEmpty(e.how)) text += "\n" + e.how;

        Show(text, feedbackDuration);
        Record("erreur", skill + ":" + error, ((int)level).ToString() + (repeatCount >= 2 ? "_repetee" : ""));
    }

    // ---------- 2. Guidage en direct et 4. hésitation ----------

    void Update()
    {
        if (tutorial == null) return;
        if (head == null && Camera.main != null) head = Camera.main.transform;

        if (held != null && (!held.IsHeld || held.IsPlaced)) held = null;

        if (held != null)
        {
            // Hésitation : pièce tenue longtemps sans être posée -> guidage forcé, même pour une compétence maîtrisée.
            bool hesitating = hesitationHelp && Time.time - grabTime > holdTimeout;
            if (hesitating && !holdShown)
            {
                holdShown = true;
                Record("hesitation", AdaptiveHelp.SkillOf(held.name), "piece_tenue");
            }
            if ((liveCoaching || hesitating) && Time.time >= nextCoachTime) Coach(held, hesitating);
            return;
        }

        // Hésitation : aucune action depuis un moment pendant une étape de pose.
        if (hesitationHelp && !idleShown && sequence != null && sequence.Current != null
            && Time.time - lastActivity > idleTimeout && !tutorial.IsShowingMessage)
        {
            idleShown = true;
            SnapToTarget cur = sequence.Current;
            Show("Besoin d'aide ? La pièce à prendre maintenant est colorée en orange.\n"
                 + "Prenez-la avec la gâchette et posez-la sur le fantôme transparent."
                 + (cur.EngineSideOk ? "" : "\nD'abord, retournez le moteur avec Y."), feedbackDuration);
            Record("hesitation", AdaptiveHelp.SkillOf(cur.name), "inactivite");
        }
    }

    private void Coach(SnapToTarget p, bool force)
    {
        nextCoachTime = Time.time + 0.3f;
        AdaptiveHelp ah = AdaptiveHelp.Instance;
        if (!force && ah != null && ah.LevelFor(p) == AdaptiveHelp.Level.Maitrise) return;
        if (!p.EngineSideOk) return;   // le retour « sens du moteur » s'en charge

        float d = p.CurrentDistance;
        if (d > coachingDistance && !force) return;

        string text;
        if (p.PositionOk && p.AngleOk) text = "C'est bon : relâchez la gâchette.";
        else if (!p.AngleOk) text = RotationAdvice(p.MissingRotation(), p.CurrentAngle)
                                    + (p.PositionOk ? "" : "\nPuis amenez-la sur le fantôme.");
        else text = "Bonne orientation. Rapprochez-la du fantôme.";

        // Le texte du panneau n'est réécrit que quand la consigne CHANGE : réécrire un texte plusieurs fois par seconde
        // fait clignoter l'affichage en réalité mixte. Sinon, on prolonge simplement le message affiché.
        if (text == lastCoachText && tutorial.IsShowingMessage) tutorial.ExtendMessage(1.5f);
        else
        {
            lastCoachText = text;
            tutorial.ShowMessage(text, 1.5f, false);
        }
    }

    // Traduit une rotation en consigne simple, dans le repère du regard de l'utilisateur.
    private string RotationAdvice(Quaternion missing, float angleError)
    {
        missing.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) { angle = 360f - angle; axis = -axis; }
        if (float.IsNaN(axis.x) || angle < 1f) return "Tournez légèrement la pièce.";

        Vector3 a = head != null ? head.InverseTransformDirection(axis) : axis;
        string amount = angle > 135f ? "d'un demi-tour" : angle > 35f ? "d'un quart de tour" : "légèrement";
        float ax = Mathf.Abs(a.x), ay = Mathf.Abs(a.y), az = Mathf.Abs(a.z);

        if (ay >= ax && ay >= az)
            return "Tournez la pièce " + amount + (angle > 135f ? " sur elle-même." : (a.y > 0 ? " vers la droite." : " vers la gauche."));
        if (ax >= az)
            return "Basculez la pièce " + amount + (angle > 135f ? " (tête en bas)." : (a.x > 0 ? " vers l'avant, loin de vous." : " vers vous."));
        return "Inclinez la pièce " + amount + (angle > 135f ? " (à l'envers)." : (a.z > 0 ? " vers la gauche." : " vers la droite."));
    }

    // ---------- Affichage et journal ----------

    private void Show(string text, float duration)
    {
        if (tutorial != null) tutorial.ShowMessage(text, duration);
        else Debug.Log("[Guide] " + text.Replace("\n", " "));
    }

    private void Record(string kind, string what, string detail)
    {
        if (logger != null) logger.LogEvent("conseil_" + kind, what, detail, "");
    }

    // ---------- Catalogue ----------

    private Entry Find(string skill, string error)
    {
        if (entries.TryGetValue(skill + "/" + error, out Entry e)) return e;
        if (entries.TryGetValue("*/" + error, out e)) return e;
        return null;
    }

    private void Add(string skill, string error, string what, string why, string how)
    {
        entries[skill + "/" + error] = new Entry { what = what, why = why, how = how };
    }

    // Fichier CSV : competence;erreur;quoi;pourquoi;comment (une ligne d'en-tête, lignes # ignorées).
    private void LoadCatalogue(string csv)
    {
        int n = 0;
        foreach (string raw in csv.Split('\n'))
        {
            string line = raw.Trim('\r', ' ');
            if (line == "" || line.StartsWith("#") || line.StartsWith("competence;")) continue;
            string[] c = line.Split(';');
            if (c.Length < 3) continue;
            Add(c[0].Trim(), c[1].Trim(), c[2].Trim(), c.Length > 3 ? c[3].Trim() : "", c.Length > 4 ? c[4].Trim() : "");
            n++;
        }
        Debug.Log("[Guide] catalogue pédagogique : " + n + " ligne(s) chargée(s).");
    }

    // Textes intégrés pour le bas moteur V6 (d'après la gamme d'assemblage).
    private void LoadDefaults()
    {
        // Erreurs communes à toutes les pièces.
        Add("*", "endroit", "La pièce n'est pas encore à sa place.", "",
            "Amenez-la exactement sur le fantôme transparent avant de relâcher.");
        Add("*", "orientation", "Bon endroit, mais la pièce n'est pas dans le bon sens.",
            "Une pièce montée de travers ne peut pas fonctionner : le moteur serait à démonter.",
            "Tournez-la pour qu'elle épouse exactement le fantôme.");
        Add("*", "mauvaise_piece", "Ce n'est pas la bonne pièce pour cet emplacement.",
            "Chaque emplacement attend une pièce précise, usinée pour lui.",
            "Prenez la pièce colorée en orange : le fantôme a sa forme.");
        Add("*", "sens_moteur", "Le moteur n'est pas dans le bon sens pour cette étape.",
            "Le bas moteur se monte bloc retourné (gamme, opération 20) : c'est la seule façon d'accéder aux paliers.",
            "Appuyez sur Y pour retourner le moteur, plan de carter vers le haut.");
        Add("*", "mauvais_outil", "Ce n'est pas le bon outil pour cette vis.",
            "Un outil inadapté abîme la tête de vis et ne permet pas de contrôler le couple.",
            "Reposez-le et prenez l'outil indiqué sur le panneau.");
        Add("*", "conseil", "suivez le fantôme pour la position et le sens.", "", "");
        Add("ordre_montage", "ordre", "Trop tôt : cette pièce ne peut pas encore être posée.",
            "La gamme fixe un ordre : une pièce posée trop tôt bloque l'accès aux suivantes ou empêche un contrôle.",
            "");

        // Erreurs d'orientation propres à chaque pièce (pourquoi = conséquence réelle).
        Add("piston", "orientation", "Le piston est monté à l'envers.",
            "Le piston a un sens : son repère va vers l'avant du moteur. À l'envers, il peut heurter les soupapes.",
            "Faites-le tourner pour que sa forme corresponde au fantôme.");
        Add("bielle", "orientation", "La bielle est dans le mauvais sens.",
            "La bielle a un sens de montage ; à l'envers, elle frotte sur le vilebrequin.",
            "Retournez-la pour qu'elle épouse le fantôme.");
        Add("chapeau", "orientation", "Le chapeau de bielle est à l'envers.",
            "Le chapeau est usiné avec SA bielle : à l'envers, l'alésage n'est plus rond et le coussinet grippe sur le maneton.",
            "Remontez-le dans le sens où il était sur la bielle.");
        Add("vilebrequin", "orientation", "Le vilebrequin est dans le mauvais sens.",
            "Le flasque de poulie doit être vers l'avant du moteur (gamme 30.2), sinon la distribution ne peut pas être montée.",
            "Tournez-le, flasque de poulie vers l'avant.");
        Add("bague", "orientation", "La bague-palier n'est pas bien alignée.",
            "La bague doit s'appuyer sur son épaulement : mal posée, le vilebrequin a du jeu.",
            "Alignez-la sur l'axe du vilebrequin.");
        Add("axe", "orientation", "L'axe de piston n'est pas aligné.",
            "L'axe doit traverser le piston et le pied de bielle : de travers, l'ensemble ne peut pas articuler.",
            "Alignez-le avec les trous du piston.");
        Add("ensemble", "orientation", "L'ensemble piston-bielle n'est pas dans l'axe du cylindre.",
            "Le piston doit descendre droit dans le cylindre, la tête de bielle sur le maneton.",
            "Inclinez-le comme le fantôme, dans l'axe du cylindre.");
        Add("ensemble", "mauvaise_piece", "Ce n'est pas l'ensemble prévu pour ce cylindre.",
            "Les ensembles piston-bielle sont appariés à leur cylindre : on ne les intervertit pas (gamme, 40.2).",
            "Prenez l'ensemble coloré en orange.");
        Add("carter", "orientation", "Le carter est mal orienté.",
            "Le carter porte les demi-paliers du vilebrequin : mal posé, le vilebrequin n'est pas maintenu.",
            "Posez-le à plat sur le plan de joint, comme le fantôme.");
        Add("vis_chc", "orientation", "La vis n'est pas dans le bon sens.",
            "La tête de la vis CHC doit être côté chapeau pour recevoir la clé six pans.",
            "Retournez la vis, tête vers l'extérieur.");
        Add("vis_carter", "orientation", "La vis n'est pas dans le bon sens.",
            "La vis entre par le carter, tête en appui sur sa rondelle.",
            "Retournez-la, tête vers l'extérieur.");
        Add("ecrou", "orientation", "L'écrou n'est pas dans l'axe de la vis.", "", "Alignez-le sur le bout de la vis.");

        // Conseils donnés AVANT l'erreur (anticipation).
        Add("piston", "conseil", "le piston a un sens de montage.", "", "Comparez sa forme avec celle du fantôme avant de le poser.");
        Add("bielle", "conseil", "la bielle a un sens de montage.", "", "Vérifiez le sens sur le fantôme.");
        Add("axe", "conseil", "l'axe traverse le piston et le pied de bielle.", "", "Alignez-le avec les trous.");
        Add("bague", "conseil", "la bague va à une extrémité du vilebrequin.", "", "Elle s'appuie sur son épaulement.");
        Add("vilebrequin", "conseil", "flasque de poulie vers l'avant du moteur.", "", "Regardez de quel côté est le flasque sur le fantôme.");
        Add("chapeau", "conseil", "le chapeau se remonte dans le sens où il était sur la bielle.", "", "Comparez avec le fantôme : il n'est pas symétrique.");
        Add("vis_chc", "conseil", "tête de vis côté chapeau.", "", "La clé six pans viendra dans cette tête.");
        Add("ecrou", "conseil", "l'écrou se visse au bout de la vis.", "", "");
        Add("ensemble", "conseil", "chaque ensemble va dans SON cylindre.", "", "Prenez l'ensemble coloré, piston vers le cylindre.");
        Add("carter", "conseil", "le carter se pose à plat sur le plan de joint.", "", "Il recouvre le vilebrequin.");
        Add("vis_carter", "conseil", "les vis du carter se serreront en croix.", "", "");

        // Serrage.
        Add("serrage", "sur_serrage", "Sur-serrage : vous avez continué après le clic.",
            "Au-delà du couple, la vis s'allonge et peut casser ; le chapeau se déforme.",
            "Relâchez dès que vous sentez la vibration du clic.");
        Add("serrage", "couple_insuffisant", "Couple insuffisant : la clé a cliqué trop tôt.",
            "Une vis pas assez serrée se desserre avec les vibrations : la bielle peut se détacher et casser le moteur.",
            "En tenant la clé, poussez le joystick vers le haut jusqu'à la valeur demandée, puis reprenez.");
        Add("ordre_serrage", "hors_sequence_croix", "Ordre de serrage : ce n'est pas la bonne vis.",
            "Serrer en croix répartit l'effort ; sinon le carter se déforme et fuit.",
            "Serrez la vis OPPOSÉE, en diagonale de la précédente.");
        Add("ordre_serrage", "hors_sequence", "Ordre de serrage non respecté.",
            "Toutes les vis doivent être au couple avant le serrage angulaire, pour un serrage homogène.",
            "Mettez d'abord toutes les vis au couple.");
    }
}
