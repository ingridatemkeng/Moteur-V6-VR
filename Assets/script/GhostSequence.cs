using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// GAMME DE MONTAGE : quelles pièces peuvent être posées, et dans quel ordre.
//
// Deux modes :
// - ORDRE STRICT (champ « Gamme » vide) : les pièces de la liste « Steps », une par une, dans l'ordre de la liste.
// - ORDRE LIBRE (fichier « Gamme » renseigné) : un GRAPHE DE PRÉCÉDENCE décrit, pour chaque pièce, les pièces
//   (ou les serrages) qui doivent être faits avant elle. Toutes les pièces dont les prérequis sont faits sont
//   disponibles en même temps : l'apprenant choisit son ordre (les « libertés » de la gamme).
//
// Fichier de gamme (texte, une ligne par pièce ou groupe de pièces) :
//     # commentaire
//     piston_head :
//     piston_pin  : piston_head, piston_rod
//     carter      : serrage:ass_pist_f, ass_pist_f.0*
//   - à gauche, le nom de la pièce ; « * » = n'importe quelle suite (camshaft_bushing* = les deux bagues) ;
//   - à droite, les prérequis séparés par des virgules ; « serrage:<nom du groupe> » attend la fin d'un serrage.
//   Les pièces de « Steps » absentes du fichier ne font pas partie de cette gamme : elles sont masquées.
//   Deux fichiers = deux gammes (gamme simple, gamme complète), sans changer le code.
//
// Dans les deux modes :
// - la ou les pièces à prendre sont colorées (sauf compétence maîtrisée, voir AdaptiveHelp) ;
// - pendant un serrage, aucune nouvelle pièce n'est proposée ;
// - une pièce posée trop tôt est une ERREUR D'ORDRE (bloquée ou laissée faire selon « Order Error Policy »).
//
// À placer sur un objet de la scène (ex. gamme1).

public class GhostSequence : MonoBehaviour
{
    public static GhostSequence Instance { get; private set; }

    [Tooltip("Toutes les pièces à poser. En ordre strict : dans l'ordre de montage.")]
    public SnapToTarget[] steps;

    [Header("Graphe de précédence (ordre libre)")]
    [Tooltip("Fichier texte de la gamme (ex. gamme_complete.txt). Vide = ordre strict de la liste Steps.")]
    public TextAsset gamme;
    public enum OrderErrorPolicy { Bloquer, LaisserFaire }
    [Tooltip("Pièce posée trop tôt : Bloquer (refusée et expliquée) ou Laisser Faire (acceptée, l'erreur est notée et expliquée).")]
    public OrderErrorPolicy orderErrorPolicy = OrderErrorPolicy.Bloquer;
    [Tooltip("Ordre libre : afficher aussi les fantômes de toutes les pièces disponibles quand on ne tient rien. Décoché : seul le fantôme de la pièce tenue s'affiche.")]
    public bool showAvailableGhostsWhenIdle = false;

    [Header("Mise en évidence de la pièce à prendre")]
    public bool highlightCurrent = true;
    public Color highlightColor = new Color(1f, 0.55f, 0f);   // orange

    // Première pièce disponible (ou null si tout est posé / serrage en cours).
    public SnapToTarget Current { get; private set; }
    // Nombre de pièces déjà posées (en ordre strict : index de l'étape en cours).
    public int CurrentIndex { get; private set; } = -1;
    public int Count => FreeOrder ? inGamme.Count : (steps != null ? steps.Length : 0);
    public bool FreeOrder => rules.Count > 0;
    public IReadOnlyList<SnapToTarget> Available => available;

    // (première pièce disponible ou null, index de l'étape, nombre total)
    public event Action<SnapToTarget, int, int> StepChanged;

    private class Rule { public Regex pattern; public string text; public List<string> prereqs = new List<string>(); }
    private readonly List<Rule> rules = new List<Rule>();
    private readonly HashSet<SnapToTarget> inGamme = new HashSet<SnapToTarget>();
    private readonly Dictionary<SnapToTarget, Rule> ruleOf = new Dictionary<SnapToTarget, Rule>();
    private readonly List<SnapToTarget> available = new List<SnapToTarget>();

    private TighteningGroup[] groups = new TighteningGroup[0];
    private string lastSignature = "";
    private SnapToTarget lastHeld;
    private MaterialPropertyBlock block;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");   // matériaux URP
    private static readonly int ColorId = Shader.PropertyToID("_Color");           // autres matériaux

    // ---------- Démarrage ----------

    void Awake()
    {
        Instance = this;
        if (gamme != null) Parse(gamme.text);

        foreach (var s in steps)
        {
            if (s == null) continue;
            if (!FreeOrder) { inGamme.Add(s); continue; }
            foreach (var r in rules)
                if (r.pattern.IsMatch(s.name.ToLowerInvariant())) { ruleOf[s] = r; inGamme.Add(s); break; }
        }

        // Pièces hors de cette gamme : masquées avec leur fantôme.
        if (FreeOrder)
            foreach (var s in steps)
                if (s != null && !inGamme.Contains(s))
                {
                    s.gameObject.SetActive(false);
                    if (s.target != null) s.target.gameObject.SetActive(false);
                }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        groups = FindObjectsByType<TighteningGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (EngineFlipper.Instance != null) EngineFlipper.Instance.onFlipped.AddListener(Refresh);
        foreach (var g in groups)
        {
            g.onActivated.AddListener(Refresh);
            g.onAllTightened.AddListener(Refresh);
        }

        foreach (var s in steps)
        {
            if (s == null) continue;
            s.onSnapped.AddListener(Refresh);
            s.onUnsnapped.AddListener(Refresh);
        }

        string mode = FreeOrder ? "libre" : "strict";
        Debug.Log("[Guide] gamme : " + (gamme != null ? gamme.name : "liste Steps") + ", ordre " + mode
                  + ", " + inGamme.Count + " pièces" + (FreeOrder ? ", erreur d'ordre : " + orderErrorPolicy : "") + ".");
        SessionLogger logger = FindFirstObjectByType<SessionLogger>(FindObjectsInactive.Include);
        if (logger != null) logger.LogEvent("gamme", gamme != null ? gamme.name : "liste", mode, orderErrorPolicy.ToString());

        Refresh();
    }

    // Lit le fichier de gamme.
    private void Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash).Trim();
            if (line == "") continue;
            int colon = line.IndexOf(':');
            // « serrage:x » peut contenir « : » : le séparateur est le premier « : » suivi d'un espace ou en fin de nom.
            int sep = line.IndexOf(" :");
            if (sep < 0) sep = colon;
            string name = (sep >= 0 ? line.Substring(0, sep) : line).Trim();
            string right = sep >= 0 ? line.Substring(sep + (line[sep] == ' ' ? 2 : 1)).Trim() : "";
            if (name == "") continue;

            var rule = new Rule { pattern = Pattern(name), text = name };
            foreach (string p in right.Split(','))
                if (p.Trim() != "") rule.prereqs.Add(p.Trim());
            rules.Add(rule);
        }
    }

    private static Regex Pattern(string glob) =>
        new Regex("^" + Regex.Escape(glob.ToLowerInvariant()).Replace("\\*", ".*") + "$");

    // ---------- Questions posées par les autres scripts ----------

    public bool IsInGamme(SnapToTarget p) => p != null && inGamme.Contains(p);

    // Un groupe de serrage fait-il partie de cette gamme ? (toutes ses pièces préalables y sont)
    public bool IsGroupInGamme(TighteningGroup g)
    {
        if (g == null || g.prerequisites == null) return true;
        foreach (var p in g.prerequisites)
            if (p != null && !IsInGamme(p)) return false;
        return true;
    }

    // La pièce peut-elle être posée maintenant ?
    public bool IsAvailable(SnapToTarget p)
    {
        if (p == null || p.IsPlaced) return false;
        foreach (var a in available)
            if (a == p || (p.interchangeable && a.interchangeable && a.Family == p.Family)) return true;
        return false;
    }

    // Ce qui manque encore avant de pouvoir poser cette pièce (texte pour l'apprenant).
    public string MissingText(SnapToTarget p)
    {
        if (TighteningInProgress()) return "terminer le serrage en cours";
        var names = new List<string>();
        if (FreeOrder)
        {
            if (!ruleOf.TryGetValue(p, out Rule rule)) return "";
            foreach (string pre in rule.prereqs)
            {
                if (PrereqDone(pre)) continue;
                string label = pre.StartsWith("serrage:", StringComparison.OrdinalIgnoreCase)
                    ? "le serrage " + (GroupNamed(pre.Substring(8))?.displayName ?? pre.Substring(8))
                    : FirstUnplacedName(pre);
                if (label != "" && !names.Contains(label)) names.Add(label);
            }
        }
        else if (Current != null) names.Add(TutorialGuide.FriendlyName(Current.name));
        return string.Join(", ", names);
    }

    // ---------- Calcul des pièces disponibles ----------

    private bool TighteningInProgress()
    {
        foreach (var g in groups)
            if (g != null && g.IsActivated && !g.IsFinished) return true;
        return false;
    }

    private bool PrereqDone(string pre)
    {
        if (pre.StartsWith("serrage:", StringComparison.OrdinalIgnoreCase))
        {
            TighteningGroup g = GroupNamed(pre.Substring(8));
            return g == null || g.IsFinished;
        }
        Regex rx = Pattern(pre);
        foreach (var s in inGamme)
            if (rx.IsMatch(s.name.ToLowerInvariant()) && !s.IsPlaced) return false;
        return true;
    }

    private string FirstUnplacedName(string pre)
    {
        Regex rx = Pattern(pre);
        foreach (var s in steps)
            if (s != null && inGamme.Contains(s) && rx.IsMatch(s.name.ToLowerInvariant()) && !s.IsPlaced)
                return TutorialGuide.FriendlyName(s.name);
        return "";
    }

    private TighteningGroup GroupNamed(string n)
    {
        foreach (var g in groups)
            if (g != null && string.Equals(g.name, n.Trim(), StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    private void ComputeAvailable()
    {
        available.Clear();
        if (TighteningInProgress()) return;
        if (!FreeOrder)
        {
            foreach (var s in steps)
                if (s != null && !s.IsPlaced)
                {
                    available.Add(s);
                    // Pièces identiques : toutes celles qui restent sont proposées.
                    if (s.interchangeable)
                        foreach (var o in steps)
                            if (o != null && o != s && !o.IsPlaced && o.interchangeable && o.Family == s.Family) available.Add(o);
                    break;
                }
            return;
        }
        foreach (var s in steps)
        {
            if (s == null || s.IsPlaced || !ruleOf.TryGetValue(s, out Rule rule)) continue;
            bool ok = true;
            foreach (string pre in rule.prereqs)
                if (!PrereqDone(pre)) { ok = false; break; }
            if (ok) available.Add(s);
        }
    }

    // ---------- Affichage ----------

    public void Refresh()
    {
        ComputeAvailable();

        int placed = 0;
        foreach (var s in steps)
        {
            if (s == null) continue;
            SetHighlight(s, false);
            if (inGamme.Contains(s) && s.IsPlaced) placed++;
        }

        SnapToTarget current = available.Count > 0 ? available[0] : null;
        bool sideOk = current == null || current.EngineSideOk;

        if (TighteningInProgress()) Debug.Log("[Guide] serrage en cours : la suite apparaîtra ensuite.");
        else if (current == null) Debug.Log("[Guide] toutes les pièces sont posées.");
        else if (!sideOk) Debug.Log("[Guide] étape en cours : " + current.name + " (retournez d'abord le moteur avec Y).");
        else Debug.Log("[Guide] pièce(s) disponible(s) : " + Names());

        // Couleur sur les pièces à prendre (l'aide adaptative peut la retirer quand la compétence est maîtrisée).
        if (highlightCurrent && sideOk)
            foreach (var a in available)
                if (AdaptiveHelp.Instance == null || AdaptiveHelp.Instance.ShouldHighlight(a)) SetHighlight(a, true);

        UpdateGhosts();

        string signature = (current != null ? current.name : "-") + "|" + Names() + "|" + sideOk;
        bool changed = signature != lastSignature;
        lastSignature = signature;
        Current = current;
        CurrentIndex = FreeOrder ? placed : (current != null ? Array.IndexOf(steps, current) : -1);
        if (changed) StepChanged?.Invoke(current, CurrentIndex, Count);
    }

    private string Names()
    {
        var n = new List<string>();
        foreach (var a in available) n.Add(a.name);
        return string.Join(", ", n);
    }

    void Update()
    {
        // Ordre libre : le fantôme suit la pièce tenue.
        SnapToTarget held = null;
        foreach (var s in steps)
            if (s != null && s.IsHeld) { held = s; break; }
        if (held != lastHeld)
        {
            lastHeld = held;
            UpdateGhosts();
        }
    }

    // Quels fantômes montrer.
    private void UpdateGhosts()
    {
        foreach (var s in steps)
            if (s != null && s.target != null && !s.IsPlaced) s.target.gameObject.SetActive(false);

        SnapToTarget current = available.Count > 0 ? available[0] : null;
        if (current == null || !current.EngineSideOk) return;

        if (!FreeOrder)
        {
            current.target.gameObject.SetActive(true);
            return;
        }

        if (lastHeld != null)
        {
            bool allowed = IsAvailable(lastHeld) || orderErrorPolicy == OrderErrorPolicy.LaisserFaire;
            if (allowed && lastHeld.target != null && lastHeld.EngineSideOk) lastHeld.target.gameObject.SetActive(true);
        }
        else if (showAvailableGhostsWhenIdle)
        {
            foreach (var a in available)
                if (a.target != null) a.target.gameObject.SetActive(true);
        }
    }

    // Colore (ou décolore) une pièce sans toucher à son matériau.
    private void SetHighlight(SnapToTarget piece, bool on)
    {
        Renderer r = piece.GetComponent<Renderer>();
        if (r == null) return;
        if (!on) { r.SetPropertyBlock(null); return; }

        if (block == null) block = new MaterialPropertyBlock();
        block.SetColor(BaseColorId, highlightColor);
        block.SetColor(ColorId, highlightColor);
        r.SetPropertyBlock(block);
    }
}
