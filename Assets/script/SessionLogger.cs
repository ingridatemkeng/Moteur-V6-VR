using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// Journal de session : enregistre chaque action de l'apprenant dans un fichier CSV.
// C'est la matière première du Machine Learning (adaptation de l'aide, anticipation des erreurs).
// - Une ligne par événement : pièce attrapée, pièce posée, erreur, astuce affichée, serrage...
// - Un fichier par session, écrit au fur et à mesure (rien n'est perdu si l'application est fermée).
// - Il se branche tout seul sur les pièces, les outils et les vis de la scène : rien à câbler.
// - Il tient aussi les compteurs du BILAN de fin de montage (étape S14 de la gamme), même sans enregistrer.
//
// À placer sur un objet de la scène (ex. « Journal »).
// Choix du participant DANS LE CASQUE, sur l'écran d'accueil (avant le premier appui sur A) :
//   joystick gauche / droite = participant précédent / suivant. P00 = vos propres tests.
//   Le dernier code choisi est retenu d'une session à l'autre.
// Fichiers : dossier « logs » de l'application
//   casque : Android/data/<nom du paquet>/files/logs/   (visible avec Meta Quest Developer Hub)
//   PC     : chemin affiché dans la Console au démarrage.

public class SessionLogger : MonoBehaviour
{
    [Header("Session")]
    [Tooltip("Code anonyme du participant (P01, P02...). Jamais de nom. Remplacé par le choix fait dans le casque si « Choose In Headset » est coché.")]
    public string participant = "P00";
    [Tooltip("Coché : le participant se choisit dans le casque, sur l'écran d'accueil (joystick gauche / droite).")]
    public bool chooseInHeadset = true;
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
    private float lastTime;           // Time.time peut revenir à 0 quand on arrête le Play : on garde la dernière valeur
    private GhostSequence sequence;
    private bool stepTouched;         // une pièce a déjà été attrapée pendant cette étape

    // Bilan de fin de montage.
    private float montageStart = -1f; // première validation du poste
    private int totalErrors;
    private int totalHints;
    private readonly Dictionary<string, int> errorsByType = new Dictionary<string, int>();

    // Choix du participant dans le casque.
    private WorkstationPlacer placer;
    private bool participantLocked;
    private int participantNumber;
    private float nextChoiceTime;
    private bool choiceShown;
    private readonly List<string> pending = new List<string>();   // lignes écrites avant le choix du participant
    private const string PrefKey = "MoteurV6_participant";

    // Prévient l'aide adaptative quand le participant est connu (son code).
    public static event Action<string> ParticipantLocked;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    void Start()
    {
        placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);

        if (chooseInHeadset && placer != null)
        {
            // On repart du dernier participant choisi (mémorisé dans le casque).
            participantNumber = PlayerPrefs.GetInt(PrefKey, ParseNumber(participant));
        }
        else LockParticipant();

        startTime = stepStartTime = Time.time;
        tutorial = FindFirstObjectByType<TutorialGuide>(FindObjectsInactive.Include);
        Subscribe();
        Write("session_ouverte", Application.version, "", "");
    }

    // ---------- Choix du participant ----------

    void Update()
    {
        if (participantLocked || placer == null) return;

        // Dès que l'utilisateur a appuyé sur A, le participant est figé.
        if (placer.CurrentState != WorkstationPlacer.State.Attente)
        {
            LockParticipant();
            return;
        }

        float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x
                + OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x;
        bool changed = false;
        if (Mathf.Abs(x) > 0.6f && Time.time >= nextChoiceTime)
        {
            participantNumber = Mathf.Clamp(participantNumber + (x > 0f ? 1 : -1), 0, 99);
            nextChoiceTime = Time.time + 0.25f;
            changed = true;
        }
        if (changed || !choiceShown) ShowChoice();
    }

    private void ShowChoice()
    {
        if (placer.instructions == null) return;
        choiceShown = true;
        placer.instructions.text = "Bienvenue\nAppuyez sur A pour faire apparaître\nle poste de montage\n\n"
            + "Participant : " + Code(participantNumber) + (participantNumber == 0 ? " (test)" : "")
            + "\n(joystick gauche / droite pour changer)";
    }

    private void LockParticipant()
    {
        if (participantLocked) return;
        participantLocked = true;
        if (chooseInHeadset && placer != null)
        {
            participant = Code(participantNumber);
            PlayerPrefs.SetInt(PrefKey, participantNumber);
            PlayerPrefs.Save();
        }
        Debug.Log("[Guide] participant : " + participant);

        if (recording)
        {
            OpenFile();
            if (writer != null)
            {
                // Les lignes écrites avant le choix reçoivent maintenant la session et le participant.
                foreach (string line in pending)
                    writer.WriteLine(line.Replace("{SESSION}", sessionId).Replace("{PARTICIPANT}", Clean(participant)));
            }
        }
        pending.Clear();

        // Une fois le fichier ouvert : l'aide adaptative peut enregistrer la maîtrise de départ.
        ParticipantLocked?.Invoke(participant);
    }

    private static string Code(int n) => "P" + n.ToString("00");

    private static int ParseNumber(string code)
    {
        string digits = "";
        foreach (char c in code ?? "") if (char.IsDigit(c)) digits += c;
        return int.TryParse(digits, out int n) ? Mathf.Clamp(n, 0, 99) : 0;
    }

    // ---------- Fichier ----------

    private void OpenFile()
    {
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
            writer = null;
        }
    }

    private void Subscribe()
    {
        // Poste et séquence.
        var placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);
        // Le temps n'est jamais remis à zéro : le poste peut être replacé (B puis A) en cours de montage.
        if (placer != null) placer.onPlacementConfirmed.AddListener(() =>
        {
            // Le temps passé à placer le poste ne compte pas dans la première étape.
            if (!stepTouched) stepStartTime = Time.time;
            if (montageStart < 0f) montageStart = Time.time;
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
            p.onWrongSide.AddListener(() => Error("erreur_sens_moteur", p.name, "", "", true));
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
                NewStep(stepIndex, "serrage_" + group.name);
                Write("serrage_disponible", group.name, "", "");
            })));
            group.onAllTightened.AddListener(() => Write("serrage_termine", group.name, "", ""));
        }

        // Réglage des clés dynamométriques (noté quand l'apprenant a fini de régler).
        foreach (var setting in FindObjectsByType<TorqueSetting>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            StartCoroutine(WatchSetting(setting));

        // Retournement du moteur et astuces du tutoriel.
        var flipper = FindFirstObjectByType<EngineFlipper>(FindObjectsInactive.Include);
        if (flipper != null) flipper.onFlipped.AddListener(() =>
            Write("moteur_retourne", flipper.IsUpsideDown ? "carter_en_haut" : "a_l_endroit", "", ""));

        TutorialGuide.HintShown += OnHintShown;
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
        totalHints++;
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

    private IEnumerator WatchSetting(TorqueSetting s)
    {
        float last = s.value;
        float changedAt = -1f;
        while (s != null)
        {
            if (!Mathf.Approximately(s.value, last)) { last = s.value; changedAt = Time.time; }
            if (changedAt > 0f && Time.time - changedAt > 0.8f)
            {
                changedAt = -1f;
                Write("cle_reglee", s.name, Num(s.value), "");
            }
            yield return null;
        }
    }

    private void Error(string evt, string obj, string v1, string v2, bool withGrabDuration = false)
    {
        errorsInStep++;
        totalErrors++;
        errorsByType[evt] = errorsByType.TryGetValue(evt, out int n) ? n + 1 : 1;
        Write(evt, obj, v1, v2, withGrabDuration);
    }

    // Pour les autres scripts (aide adaptative) : ajoute une ligne au journal.
    public void LogEvent(string evt, string obj, string v1, string v2) => Write(evt, obj, v1, v2);

    // ---------- Bilan (étape S14) ----------

    // Texte du bilan affiché à la fin du montage, et ligne « bilan » dans le journal.
    public string Summary()
    {
        float start = montageStart >= 0f ? montageStart : startTime;
        float seconds = Mathf.Max(0f, Mathf.Max(Time.time, lastTime) - start);
        int min = Mathf.FloorToInt(seconds / 60f);
        int sec = Mathf.FloorToInt(seconds % 60f);

        string details = "";
        foreach (var kv in errorsByType)
            details += (details == "" ? "" : ", ") + ErrorLabel(kv.Key) + " " + kv.Value;

        Write("bilan", "", Num(seconds), totalErrors.ToString(Inv));

        return "Temps : " + min + " min " + sec.ToString("00") + " s\n"
               + "Erreurs : " + totalErrors + (details == "" ? "" : " (" + details + ")") + "\n"
               + "Astuces affichées : " + totalHints;
    }

    private static string ErrorLabel(string evt)
    {
        switch (evt)
        {
            case "erreur_orientation":        return "orientation";
            case "erreur_endroit":            return "emplacement";
            case "erreur_mauvaise_piece":     return "mauvaise pièce";
            case "erreur_mauvais_outil":      return "mauvais outil";
            case "erreur_sur_serrage":        return "sur-serrage";
            case "erreur_couple_insuffisant": return "couple insuffisant";
            case "erreur_hors_sequence":      return "ordre de serrage";
            case "erreur_sens_moteur":        return "sens du moteur";
            default:                          return evt.Replace("erreur_", "");
        }
    }

    // ---------- Écriture ----------

    private void Write(string evt, string obj, string v1, string v2, bool withGrabDuration = false)
    {
        bool waiting = recording && !participantLocked;   // participant pas encore choisi : on garde la ligne de côté
        if (writer == null && !waiting) return;
        float now = Mathf.Max(Time.time, lastTime);
        lastTime = now;
        try
        {
            string line = string.Join(";",
                waiting ? "{SESSION}" : sessionId,
                waiting ? "{PARTICIPANT}" : Clean(participant),
                Num(now - startTime),
                (stepIndex + 1).ToString(Inv),
                Clean(stepPiece),
                Num(now - stepStartTime),
                evt,
                Clean(obj),
                v1,
                v2,
                withGrabDuration ? Num(now - grabTime) : "",
                errorsInStep.ToString(Inv),
                hintsInStep.ToString(Inv),
                (tutorial != null && tutorial.showHints) ? "1" : "0");
            if (waiting) pending.Add(line);
            else writer.WriteLine(line);
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
