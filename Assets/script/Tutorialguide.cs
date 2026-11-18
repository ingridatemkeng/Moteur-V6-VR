using System.Collections.Generic;
using UnityEngine;
using TMPro;

// Tutoriel contextuel : les explications apparaissent au fur et à mesure que l'utilisateur avance dans le montage.
// - Une consigne d'étape toujours affichée (« Étape 3/9 : posez l'axe de piston »).
// - Des astuces affichées UNE seule fois, la première fois qu'une situation se présente
//   (première pièce attrapée, première erreur d'orientation, premier clic de serrage...).
// Les messages s'affichent sur le panneau de consignes (le même que celui de l'écran d'accueil).
//
// À placer sur un objet de la scène (ex. « Tutoriel »). Il trouve tout seul les pièces et les outils.

public class TutorialGuide : MonoBehaviour
{
    [Header("Références")]
    [Tooltip("Le panneau de consignes (Text_instruction_affichage).")]
    public TMP_Text panel;
    public WorkstationPlacer placer;
    public GhostSequence sequence;
    [Tooltip("Laisser vide : tous les groupes de serrage de la scène sont trouvés automatiquement.")]
    public TighteningGroup tighteningGroup;
    private TighteningGroup[] groups = new TighteningGroup[0];

    [Header("Réglages")]
    [Tooltip("Décocher pour un utilisateur expérimenté ou pour le mode Évaluation : seules les consignes d'étape restent.")]
    public bool showHints = true;
    [Tooltip("Durée d'affichage d'une astuce (s) avant de revenir à la consigne d'étape.")]
    public float hintDuration = 7f;

    private string stepText = "";
    private string hintText = "";
    private float hintUntil;
    private readonly HashSet<string> shown = new HashSet<string>();

    // Prévient le journal de session qu'une astuce vient d'être affichée (clé de l'astuce).
    public static event System.Action<string> HintShown;

    void Start()
    {
        // « Include » : le contenu du poste est masqué au démarrage, il faut quand même le trouver.
        if (placer == null) placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);
        if (sequence == null) sequence = FindFirstObjectByType<GhostSequence>(FindObjectsInactive.Include);
        groups = FindObjectsByType<TighteningGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (panel == null && placer != null) panel = placer.instructions;

        int pieces = FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        int tools = FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        Debug.Log("[Tuto] démarrage : panneau " + (panel != null ? panel.name : "INTROUVABLE")
                  + ", poste " + (placer != null ? "ok" : "INTROUVABLE")
                  + ", séquence " + (sequence != null ? "ok" : "absente")
                  + ", " + groups.Length + " groupe(s) de serrage"
                  + ", " + pieces + " pièces, " + tools + " outils.");

        if (placer != null) placer.onPlacementConfirmed.AddListener(OnPlacementConfirmed);
        if (sequence != null) sequence.StepChanged += OnStepChanged;

        // Toutes les pièces à poser (y compris celles désactivées au démarrage).
        foreach (var piece in FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            piece.onGrab.AddListener(() => Hint("grab",
                "Le fantôme transparent montre où va la pièce.\nPlacez-la dessus, dans la même orientation,\npuis relâchez la gâchette."));
            piece.onWrongOrientation.AddListener(() => Hint("orientation",
                "Bon endroit, mais mauvaise orientation :\ntournez la pièce pour qu'elle épouse le fantôme.\nLa console (manette gauche) indique l'écart en degrés."));
            piece.onWrongPlace.AddListener(() => Hint("place",
                "Pas encore au bon endroit :\namenez la pièce sur le fantôme avant de la relâcher."));
            piece.onWrongPiece.AddListener(() => Hint("wrong-piece",
                "Mauvaise pièce : cet emplacement en attend une autre.\nRegardez le fantôme : il a la forme de la pièce attendue."));
            piece.onSnapped.AddListener(() => Hint("snap",
                "Bravo, la pièce est en place et verrouillée.\nLe fantôme suivant indique la prochaine pièce."));
        }

        // Outils.
        foreach (var tool in FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Tool t = tool;
            t.onGrabbed.AddListener(() =>
            {
                if (AllFinished()) return;
                if (groups.Length > 0 && ActiveGroup() == null) Hint("tool-early",
                    "Cet outil servira au serrage.\nPosez d'abord les pièces demandées.");
                else Hint("tool-grab",
                    "Approchez le bout de l'outil de la tête d'une vis :\nil s'y engage tout seul.");
            });
            t.onEngaged.AddListener(() => Hint("engaged",
                "La clé est dans la vis. Faites pivoter le manche\ndans le sens du vissage (aiguilles d'une montre).\nVous pouvez la lâcher : elle reste dans la vis.\nPour la retirer, attrapez-la et tirez-la\ndans l'axe de la vis, vers l'extérieur."));
            t.onWrongTool.AddListener(() => Hint("wrong-tool",
                "Ce n'est pas le bon outil (voir la console).\nReposez-le et prenez " + ExpectedToolText() + "."));
        }

        // Réglage de la clé dynamométrique : la valeur s'affiche sur le panneau pendant qu'on règle.
        TorqueSetting.ValueChanged += OnTorqueSettingChanged;

        // Serrage : un ou plusieurs groupes (bielle, carter...).
        bool hasFlipper = FindFirstObjectByType<EngineFlipper>(FindObjectsInactive.Include) != null;
        foreach (var g in groups)
        {
            TighteningGroup group = g;
            group.onActivated.AddListener(() =>
                SetStep("Serrage" + (string.IsNullOrEmpty(group.displayName) ? "" : " " + group.displayName)
                        + " : prenez " + ToolText(group) + " sur l'établi\net serrez chaque vis au couple"
                        + TorqueText(group) + "."
                        + (NeedsSetting(group) ? "\nRéglez d'abord la clé : en la tenant, joystick haut / bas." : "")
                        + (group.crossPattern ? "\nSerrez en croix : après une vis, passez à la vis opposée." : "")));
            group.onAllTightened.AddListener(() =>
            {
                // S'il reste des pièces à poser, la consigne de l'étape suivante prend le relais.
                if (!AllFinished()) return;
                SetStep("Montage terminé, bravo !\nLe bas moteur est monté et serré."
                        + (hasFlipper ? "\nAppuyez sur Y pour retourner le moteur." : ""));
            });

            foreach (var f in group.fasteners)
            {
                if (f == null) continue;
                f.onTorqueWindowEntered.AddListener(() => Hint("click",
                    "Clic ! Le couple est atteint : relâchez la gâchette.\nPassez ensuite à la vis suivante."));
                f.onTorqueValidated.AddListener(() =>
                {
                    if (f.angleStep && group.AllTorqued())
                        SetStep("Toutes les vis sont au couple.\nSerrage angulaire : tournez chaque vis de "
                                + f.targetAngle.ToString("F0") + "° de plus.");
                });
                f.onAngleReached.AddListener(() => Hint("angle", "Angle atteint : relâchez la gâchette."));
                f.onOutOfSequence.AddListener(() =>
                {
                    if (group.crossPattern) Hint("sequence-croix",
                        "Serrage en croix : après une vis,\nserrez la vis OPPOSÉE (en diagonale),\npas sa voisine.");
                    else Hint("sequence",
                        "Ordre de serrage : mettez d'abord TOUTES les vis au couple,\navant le serrage angulaire.");
                });
                f.onOverTightened.AddListener(() => Hint("over",
                    "Sur-serrage : relâchez dès que vous sentez\nla vibration du clic."));
                f.onUnderTightened.AddListener(() => Hint("under",
                    "Couple insuffisant : la clé est réglée trop bas.\nEn la tenant, poussez le joystick vers le haut\njusqu'à la valeur demandée, puis reprenez."));
            }
        }
    }

    void OnDestroy()
    {
        if (sequence != null) sequence.StepChanged -= OnStepChanged;
        TorqueSetting.ValueChanged -= OnTorqueSettingChanged;
    }

    private void OnTorqueSettingChanged(TorqueSetting setting, float value)
    {
        // Consigne du serrage en cours (s'il y en a un), pour que l'apprenant sache où s'arrêter.
        string target = "";
        TighteningGroup g = ActiveGroup();
        if (g != null)
            foreach (var f in g.fasteners)
                if (f != null) { target = "\nValeur demandée : " + f.targetTorque.ToString("F0") + " N·m"; break; }

        // Affichage temporaire (pas une astuce « une seule fois ») : il suit chaque cran.
        hintText = "Clé réglée sur " + value.ToString("F0") + " N·m" + target;
        hintUntil = Time.time + 2.5f;
        Refresh();
    }

    // ---------- Déroulé ----------

    private void OnPlacementConfirmed()
    {
        Debug.Log("[Tuto] poste verrouillé : le tutoriel commence.");
        if (sequence != null && sequence.Current != null)
            OnStepChanged(sequence.Current, sequence.CurrentIndex, sequence.Count);
        Hint("start",
            "La pièce à prendre est colorée en orange.\nAttrapez-la avec la gâchette avant ou latérale,\npuis posez-la sur le fantôme transparent.");
    }

    private void OnStepChanged(SnapToTarget piece, int index, int count)
    {
        if (piece == null) return;   // tout est posé : c'est le serrage qui prend la suite
        string advice = Advice(piece.name);
        SetStep("Étape " + (index + 1) + "/" + count + " : posez " + FriendlyName(piece.name) + "."
                + (string.IsNullOrEmpty(advice) ? "" : "\n" + advice));
    }

    // Le groupe en cours de serrage (activé, pas encore terminé), ou null.
    private TighteningGroup ActiveGroup()
    {
        foreach (var g in groups)
            if (g != null && g.IsActivated && !g.IsFinished) return g;
        return null;
    }

    // Tout est-il fini ? (toutes les pièces posées et tous les serrages terminés)
    private bool AllFinished()
    {
        if (groups.Length == 0) return false;
        foreach (var g in groups)
            if (g != null && !g.IsFinished) return false;
        if (sequence != null && sequence.steps != null)
            foreach (var p in sequence.steps)
                if (p != null && !p.IsPlaced) return false;
        return true;
    }

    private static string ToolText(TighteningGroup g)
    {
        if (g != null)
            foreach (var f in g.fasteners)
                if (f != null) return ToolNames.WithArticle(f.requiredTool);
        return "l'outil adapté";
    }

    // La clé attendue par ce groupe est-elle une clé réglable (composant TorqueSetting) ?
    private static bool NeedsSetting(TighteningGroup g)
    {
        if (g == null) return false;
        ToolType expected = ToolType.CleDynamometrique;
        bool found = false;
        foreach (var f in g.fasteners)
            if (f != null) { expected = f.requiredTool; found = true; break; }
        if (!found) return false;
        foreach (var t in FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.type == expected && t.GetComponent<TorqueSetting>() != null) return true;
        return false;
    }

    private static string TorqueText(TighteningGroup g)
    {
        if (g != null)
            foreach (var f in g.fasteners)
                if (f != null) return " (" + f.targetTorque.ToString("F0") + " N·m)";
        return "";
    }

    // Outil attendu en ce moment : celui du serrage en cours, sinon celui du premier groupe.
    private string ExpectedToolText()
    {
        TighteningGroup g = ActiveGroup();
        if (g == null && groups.Length > 0) g = groups[0];
        return ToolText(g);
    }

    // ---------- Noms et conseils des pièces ----------

    private static string FriendlyName(string n)
    {
        string s = n.ToLowerInvariant();
        if (s.Contains("bushing") || s.Contains("bague")) return "une bague-palier";
        if (s.StartsWith("em_")) return "l'ensemble piston-bielle " + n.Substring(3).ToUpperInvariant();
        if (s.StartsWith("ass_pist")) return "un ensemble piston-bielle";
        if (s.StartsWith("ass_pist")) return "un ensemble piston-bielle";
        if (s.Contains("crank")) return "le vilebrequin";
        if (s.Contains("cap_rod") || s.Contains("cap rod")) return "le chapeau de bielle";
        if (s.Contains("piston_pin")) return "l'axe de piston";
        if (s.Contains("piston_rod")) return "la bielle";
        if (s.Contains("piston_head")) return "le piston";
        if (s.Contains("socket head")) return "une vis CHC";
        if (s.Contains("screw") || s.Contains("bolt") || s.Contains("vis")) return "une vis du carter";
        if (s.Contains("nut")) return "un écrou";
        if (s.Contains("oil_pan") || s.Contains("carter")) return "le carter inférieur";
        return n;
    }

    private static string Advice(string n)
    {
        string s = n.ToLowerInvariant();
        if (s.Contains("bushing") || s.Contains("bague")) return "Elle se place à une extrémité du vilebrequin,\nen appui sur son épaulement.";
        if (s.StartsWith("em_")) return "Chaque ensemble va dans SON cylindre :\nvérifiez le repère avant de le poser.";
        if (s.StartsWith("ass_pist")) return "Descendez le piston dans le cylindre indiqué par le fantôme,\nla tête de bielle sur le vilebrequin.";
        if (s.StartsWith("ass_pist")) return "Descendez-le dans le cylindre indiqué par le fantôme,\ntête de bielle sur le vilebrequin.";
        if (s.Contains("oil_pan") || s.Contains("carter")) return "Il ferme le bas du bloc et maintient le vilebrequin.";
        if (s.Contains("crank")) return "Flasque de poulie vers l'avant du moteur.";
        if (s.Contains("cap_rod") || s.Contains("cap rod")) return "Respectez son sens : il se remonte comme il était sur la bielle.";
        if (s.Contains("piston_pin")) return "L'axe peut tourner sur lui-même : seule sa direction compte.";
        if (s.Contains("piston_head")) return "Attention au sens de montage du piston.";
        if (s.Contains("socket head")) return "La vis traverse la bielle et le chapeau, tête du bon côté.";
        if (s.Contains("screw") || s.Contains("bolt") || s.Contains("vis")) return "Elle fixe le carter sur le bloc.";
        if (s.Contains("nut")) return "L'écrou se place au bout de la vis.";
        return "";
    }

    // ---------- Affichage ----------

    private void SetStep(string text)
    {
        stepText = text;
        Refresh();
    }

    private void Hint(string key, string text)
    {
        if (!showHints) { Debug.Log("[Tuto] astuces désactivées (Show Hints décoché)."); return; }
        if (shown.Contains(key)) return;
        shown.Add(key);
        hintText = text;
        hintUntil = Time.time + hintDuration;
        Refresh();
        HintShown?.Invoke(key);
    }

    void Update()
    {
        if (hintText != "" && Time.time > hintUntil)
        {
            hintText = "";
            Refresh();
        }
    }

    private void Refresh()
    {
        string text = hintText != "" ? hintText : stepText;
        if (panel == null) { Debug.LogWarning("[Tuto] pas de panneau pour afficher : " + text); return; }
        // Pendant le placement du poste, c'est le WorkstationPlacer qui utilise le panneau.
        if (placer != null && placer.CurrentState != WorkstationPlacer.State.Verrouille) return;
        if (!panel.gameObject.activeInHierarchy) panel.gameObject.SetActive(true);
        panel.text = text;
        // Le réglage de la clé change à chaque cran : on ne le recopie pas dans la console.
        if (text != "" && !text.StartsWith("Clé réglée")) Debug.Log("[Tuto] affiché : " + text.Replace("\n", " "));
    }
}
