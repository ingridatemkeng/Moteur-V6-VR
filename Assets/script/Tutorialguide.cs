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
    public TighteningGroup tighteningGroup;

    [Header("Réglages")]
    [Tooltip("Décocher pour un utilisateur expérimenté ou pour le mode Évaluation : seules les consignes d'étape restent.")]
    public bool showHints = true;
    [Tooltip("Durée d'affichage d'une astuce (s) avant de revenir à la consigne d'étape.")]
    public float hintDuration = 7f;

    private string stepText = "";
    private string hintText = "";
    private float hintUntil;
    private readonly HashSet<string> shown = new HashSet<string>();

    void Start()
    {
        // « Include » : le contenu du poste est masqué au démarrage, il faut quand même le trouver.
        if (placer == null) placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);
        if (sequence == null) sequence = FindFirstObjectByType<GhostSequence>(FindObjectsInactive.Include);
        if (tighteningGroup == null) tighteningGroup = FindFirstObjectByType<TighteningGroup>(FindObjectsInactive.Include);
        if (panel == null && placer != null) panel = placer.instructions;

        int pieces = FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        int tools = FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        Debug.Log("[Tuto] démarrage : panneau " + (panel != null ? panel.name : "INTROUVABLE")
                  + ", poste " + (placer != null ? "ok" : "INTROUVABLE")
                  + ", séquence " + (sequence != null ? "ok" : "absente")
                  + ", serrage " + (tighteningGroup != null ? "ok" : "absent")
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
                if (tighteningGroup != null && !TighteningActive()) Hint("tool-early",
                    "Cet outil servira au serrage.\nPosez d'abord toutes les pièces.");
                else Hint("tool-grab",
                    "Approchez le bout de l'outil de la tête d'une vis :\nil s'y engage tout seul.");
            });
            t.onEngaged.AddListener(() => Hint("engaged",
                "Faites pivoter le manche autour de la vis,\ndans le sens du vissage (sens des aiguilles d'une montre).\nFaites des allers-retours : au retour, la vis ne tourne pas."));
            t.onWrongTool.AddListener(() => Hint("wrong-tool",
                "Ce n'est pas le bon outil (voir la console).\nReposez-le et prenez " + ExpectedToolText() + "."));
        }

        // Serrage.
        if (tighteningGroup != null)
        {
            tighteningGroup.onActivated.AddListener(() =>
                SetStep("Serrage : prenez " + ExpectedToolText() + " sur l'établi\net serrez chaque vis au couple."));
            tighteningGroup.onAllTightened.AddListener(() =>
                SetStep("Montage terminé, bravo !\nL'ensemble piston-bielle est monté et serré."));

            foreach (var f in tighteningGroup.fasteners)
            {
                if (f == null) continue;
                f.onTorqueWindowEntered.AddListener(() => Hint("click",
                    "Clic ! Le couple est atteint : relâchez la gâchette.\nPassez ensuite à l'autre vis."));
                f.onTorqueValidated.AddListener(() =>
                {
                    if (tighteningGroup.AllTorqued())
                        SetStep("Toutes les vis sont au couple.\nSerrage angulaire : tournez chaque vis de "
                                + f.targetAngle.ToString("F0") + "° de plus.");
                });
                f.onAngleReached.AddListener(() => Hint("angle", "Angle atteint : relâchez la gâchette."));
                f.onOutOfSequence.AddListener(() => Hint("sequence",
                    "Ordre de serrage : mettez d'abord TOUTES les vis au couple,\navant le serrage angulaire."));
                f.onOverTightened.AddListener(() => Hint("over",
                    "Sur-serrage : relâchez dès que vous sentez\nla vibration du clic."));
                f.onUnderTightened.AddListener(() => Hint("under",
                    "Couple insuffisant : vérifiez le réglage de la clé\npuis reprenez le serrage."));
            }
        }
    }

    void OnDestroy()
    {
        if (sequence != null) sequence.StepChanged -= OnStepChanged;
    }

    // ---------- Déroulé ----------

    private void OnPlacementConfirmed()
    {
        Debug.Log("[Tuto] poste verrouillé : le tutoriel commence.");
        if (sequence != null && sequence.Current != null)
            OnStepChanged(sequence.Current, sequence.CurrentIndex, sequence.Count);
        Hint("start",
            "Les pièces sont posées à côté du moteur.\nAttrapez-les avec la gâchette avant ou latérale.\nSuivez le fantôme transparent.");
    }

    private void OnStepChanged(SnapToTarget piece, int index, int count)
    {
        if (piece == null) return;   // tout est posé : c'est le serrage qui prend la suite
        string advice = Advice(piece.name);
        SetStep("Étape " + (index + 1) + "/" + count + " : posez " + FriendlyName(piece.name) + "."
                + (string.IsNullOrEmpty(advice) ? "" : "\n" + advice));
    }

    private bool TighteningActive()
    {
        foreach (var f in tighteningGroup.fasteners)
            if (f != null && f.CurrentPhase != NutTightening.Phase.Inactif) return true;
        return false;
    }

    private string ExpectedToolText()
    {
        if (tighteningGroup != null)
            foreach (var f in tighteningGroup.fasteners)
                if (f != null) return ToolNames.WithArticle(f.requiredTool);
        return "l'outil adapté";
    }

    // ---------- Noms et conseils des pièces ----------

    private static string FriendlyName(string n)
    {
        string s = n.ToLowerInvariant();
        if (s.Contains("crank")) return "le vilebrequin";
        if (s.Contains("cap_rod") || s.Contains("cap rod")) return "le chapeau de bielle";
        if (s.Contains("piston_pin")) return "l'axe de piston";
        if (s.Contains("piston_rod")) return "la bielle";
        if (s.Contains("piston_head")) return "le piston";
        if (s.Contains("screw")) return "une vis CHC";
        if (s.Contains("nut")) return "un écrou";
        if (s.Contains("oil_pan")) return "le carter d'huile";
        return n;
    }

    private static string Advice(string n)
    {
        string s = n.ToLowerInvariant();
        if (s.Contains("crank")) return "Il se pose dans les paliers, en bas du bloc.";
        if (s.Contains("cap_rod") || s.Contains("cap rod")) return "Respectez son sens : il se remonte comme il était sur la bielle.";
        if (s.Contains("piston_pin")) return "L'axe peut tourner sur lui-même : seule sa direction compte.";
        if (s.Contains("piston_head")) return "Attention au sens de montage du piston.";
        if (s.Contains("screw")) return "La vis traverse la bielle et le chapeau, tête du bon côté.";
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
        if (text != "") Debug.Log("[Tuto] affiché : " + text.Replace("\n", " "));
    }
}