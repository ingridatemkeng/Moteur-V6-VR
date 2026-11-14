using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// Journal de session : enregistre chaque action de l'apprenant dans un fichier CSV.
// C'est la matière première du Machine Learning (adaptation de l'aide, anticipation des erreurs).
// - Une ligne par événement : pièce attrapée, pièce posée, erreur, astuce affichée, serrage...
// - Un fichier par session, écrit au fur et à mesure (rien n'est perdu si l'application est fermée).
// - Il se branche tout seul sur les pièces, les outils et les vis de la scène : rien à câbler.
//
// À placer sur un objet de la scène (ex. « Journal »). Renseigner Participant avant chaque passage.
// Fichiers : dossier « logs » de l'application
//   casque : Android/data/<nom du paquet>/files/logs/   (visible avec Meta Quest Developer Hub)
//   PC     : chemin affiché dans la Console au démarrage.

public class SessionLogger : MonoBehaviour
{
    [Header("Session")]
    [Tooltip("Code anonyme du participant (P01, P02...). Jamais de nom.")]
    public string participant = "P00";
    [Tooltip("Décocher pour ne rien enregistrer (démonstration).")]
    public bool recording = true;
    [Tooltip("Une pièce relâchée à plus de cette distance (m) de son fantôme n'est pas comptée comme une erreur : c'est une simple reprise en main.")]
    public float attemptDistance = 0.10f;

    private StreamWriter writer;
    private string sessionId;
    private float startTime;          // ouverture de la session
    private float stepStartTime;
    private float grabTime;
    private int stepIndex = -1;
    private string stepPiece = "";
    private int errorsInStep;
    private int hintsInStep;
    private TutorialGuide tutorial;
    private GhostSequence sequence;
    private bool stepTouched;         // une pièce a déjà été attrapée pendant cette étape

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    void Start()
    {
        if (!recording) return;

        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = Path.Combine(Application.persistentDataPath, "logs");
        string path = Path.Combine(dir, "session_" + sessionId + "_" + participant + ".csv");
        try
        {
            Directory.CreateDirectory(dir);
            writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
            writer.WriteLine("session;participant;temps_s;etape;piece_etape;temps_etape_s;evenement;objet;"
                             + "valeur1;valeur2;duree_prise_s;erreurs_etape;astuces_etape;astuces_actives");
            Debug.Log("[Guide] journal de session : " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Guide] journal de session impossible à créer : " + e.Message);
            return;
        }

        startTime = stepStartTime = Time.time;
        tutorial = FindFirstObjectByType<TutorialGuide>(FindObjectsInactive.Include);

        // Poste et séquence.
        var placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);
        // Le temps n'est jamais remis à zéro : le poste peut être replacé (B puis A) en cours de montage.
        if (placer != null) placer.onPlacementConfirmed.AddListener(() =>
        {
            // Le temps passé à placer le poste ne compte pas dans la première étape.
            if (!stepTouched) stepStartTime = Time.time;
            Write("poste_verrouille", "", "", "");
        });

        sequence = FindFirstObjectByType<GhostSequence>(FindObjectsInactive.Include);
        if (sequence != null) sequence.StepChanged += OnStepChanged;

        // Pièces.
        foreach (var piece in FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            SnapToTarget p = piece;
            p.onGrab.AddListener(() => { grabTime = Time.time; stepTouched = true; Write("piece_attrapee", p.name, "", ""); });
            p.onSnapped.AddListener(() => Write("piece_posee", p.name, Cm(p.LastDistance), Deg(p.LastAngle), true));
            p.onWrongOrientation.AddListener(() => Error("erreur_orientation", p.name, Cm(p.LastDistance), Deg(p.LastAngle), true));
            p.onWrongPlace.AddListener(() =>
            {
                // Loin du fantôme : l'apprenant a seulement reposé ou repris la pièce, ce n'est pas une tentative.
                if (p.LastDistance > attemptDistance) Write("piece_relachee_loin", p.name, Cm(p.LastDistance), Deg(p.LastAngle), true);
                else Error("erreur_endroit", p.name, Cm(p.LastDistance), Deg(p.LastAngle), true);
            });
            p.onWrongPiece.AddListener(() => Error("erreur_mauvaise_piece", p.name, Cm(p.LastDistance), Deg(p.LastAngle), true));
            p.onUnsnapped.AddListener(() => Write("piece_retiree", p.name, "", ""));
        }

        // Outils.
        foreach (var tool in FindObjectsByType<Tool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Tool t = tool;
            t.onGrabbed.AddListener(() => { grabTime = Time.time; Write("outil_attrape", t.name, "", ""); });
            t.onEngaged.AddListener(() => Write("outil_engage", t.name, "", ""));
            t.onWrongTool.AddListener(() => Error("erreur_mauvais_outil", t.name, "", ""));
        }

        // Serrage.
        foreach (var fastener in FindObjectsByType<NutTightening>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            NutTightening f = fastener;
            f.onTorqueWindowEntered.AddListener(() => Write("serrage_clic", f.name, Num(f.CurrentTorque), Num(f.targetTorque)));
            f.onTorqueValidated.AddListener(() => Write("serrage_couple_valide", f.name, Num(f.CurrentTorque), Num(f.targetTorque)));
            f.onAngleValidated.AddListener(() => Write("serrage_angle_valide", f.name, Num(f.CurrentAngle), Num(f.targetAngle)));
            f.onOverTightened.AddListener(() => Error("erreur_sur_serrage", f.name, Num(f.CurrentTorque), Num(f.targetTorque)));
            f.onUnderTightened.AddListener(() => Error("erreur_couple_insuffisant", f.name, Num(f.CurrentTorque), Num(f.targetTorque)));
            f.onOutOfSequence.AddListener(() => Error("erreur_hors_sequence", f.name, "", ""));
        }

        foreach (var g in FindObjectsByType<TighteningGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            TighteningGroup group = g;
            group.onActivated.AddListener(() => StartCoroutine(NextFrame(() =>
            {
                NewStep(sequence != null ? sequence.Count : stepIndex + 1, "serrage");
                Write("serrage_disponible", group.name, "", "");
            })));
            group.onAllTightened.AddListener(() => Write("serrage_termine", group.name, "", ""));
        }

        // Retournement du moteur et astuces du tutoriel.
        var flipper = FindFirstObjectByType<EngineFlipper>(FindObjectsInactive.Include);
        if (flipper != null) flipper.onFlipped.AddListener(() => Write("moteur_retourne", flipper.IsFlipped ? "haut" : "bas", "", ""));

        TutorialGuide.HintShown += OnHintShown;

        Write("session_ouverte", Application.version, "", "");
    }

    void OnDestroy()
    {
        TutorialGuide.HintShown -= OnHintShown;
        if (writer != null)
        {
            Write("session_fermee", "", "", "");
            writer.Dispose();
            writer = null;
        }
    }

    // Sur le casque, l'application est mise en pause quand on retire le casque : on le note.
    void OnApplicationPause(bool paused)
    {
        if (writer != null) Write(paused ? "pause" : "reprise", "", "", "");
    }

    // ---------- Événements ----------

    private void OnStepChanged(SnapToTarget piece, int index, int count)
    {
        if (piece == null) return;                 // tout est posé : l'étape « serrage » prend la suite
        StartCoroutine(NextFrame(() =>
        {
            NewStep(index, piece.name);
            Write("etape_debut", piece.name, (index + 1).ToString(Inv), count.ToString(Inv));
        }));
    }

    // Le changement d'étape est noté à l'image suivante : ainsi la ligne « piece_posee »
    // reste toujours rattachée à l'étape qu'elle termine (avec son nombre d'erreurs).
    private IEnumerator NextFrame(Action action)
    {
        yield return null;
        action();
    }

    private void OnHintShown(string key)
    {
        hintsInStep++;
        Write("astuce_affichee", key, "", "");
    }

    private void NewStep(int index, string piece)
    {
        stepIndex = index;
        stepPiece = piece;
        stepStartTime = Time.time;
        errorsInStep = 0;
        hintsInStep = 0;
        stepTouched = false;
    }

    private void Error(string evt, string obj, string v1, string v2, bool withGrabDuration = false)
    {
        errorsInStep++;
        Write(evt, obj, v1, v2, withGrabDuration);
    }

    // ---------- Écriture ----------

    private void Write(string evt, string obj, string v1, string v2, bool withGrabDuration = false)
    {
        if (writer == null) return;
        try
        {
            writer.WriteLine(string.Join(";",
                sessionId,
                Clean(participant),
                Num(Time.time - startTime),
                (stepIndex + 1).ToString(Inv),
                Clean(stepPiece),
                Num(Time.time - stepStartTime),
                evt,
                Clean(obj),
                v1,
                v2,
                withGrabDuration ? Num(Time.time - grabTime) : "",
                errorsInStep.ToString(Inv),
                hintsInStep.ToString(Inv),
                (tutorial != null && tutorial.showHints) ? "1" : "0"));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Guide] journal de session : écriture impossible (" + e.Message + ").");
            writer = null;
        }
    }

    private static string Num(float v) => v.ToString("F2", Inv);
    private static string Cm(float meters) => (meters * 100f).ToString("F1", Inv);
    private static string Deg(float degrees) => degrees.ToString("F0", Inv);
    private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace(";", ",").Replace("\n", " ");
}
