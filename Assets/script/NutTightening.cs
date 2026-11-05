using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using TMPro;

// À placer sur chaque élément à serrer : vis CHC (clé Allen) ou écrou (clé à douille).
// Serrage symbolique en deux phases : au COUPLE (jauge) puis à l'ANGLE (compteur).
// L'écrou est une cible d'outil (ToolTarget) : il attend une clé (champ Required Tool).
// La rotation vient de l'outil engagé. Effet cliquet : seule la rotation dans le sens du serrage
// compte, et elle s'additionne d'une prise en main à l'autre.

public class NutTightening : ToolTarget
{
    public enum Phase { Inactif, Couple, Angle, Termine }

    [Header("Consignes (issues de la séquence de montage)")]
    [Tooltip("Couple attendu pour CET écrou (N·m). La clé, elle, clique à la valeur réglée par l'apprenant.")]
    public float targetTorque = 35f;          // N·m
    public float torqueTolerance = 2f;        // ± N·m
    public float degreesToReachTorque = 180f; // rotation nécessaire pour atteindre le couple cible
    public float targetAngle = 90f;           // degrés
    public float angleTolerance = 10f;        // ± degrés

    [Header("Comportement de la clé")]
    [Tooltip("Après le clic, la clé « décroche » : la rotation supplémentaire ne fait presque plus monter le couple (0,1 = 10 %).")]
    public float postClickGain = 0.1f;
    [Tooltip("Rotation tolérée après le clic (degrés). Au-delà : sur-serrage.")]
    public float maxDegreesAfterClick = 30f;

    [Header("Vibration des manettes")]
    public bool haptics = true;

    public enum AxisShape { Ecrou, Vis }

    [Header("Géométrie")]
    [Tooltip("Coché : l'axe de la vis est trouvé automatiquement. Décoché : utilise Local Axis.")]
    public bool autoDetectAxis = true;
    [Tooltip("Écrou : l'axe est sa dimension la plus FINE. Vis : l'axe est sa dimension la plus LONGUE.")]
    public AxisShape axisShape = AxisShape.Vis;
    [Tooltip("Axe de la vis dans le repère LOCAL de l'écrou : (1,0,0)=X rouge, (0,1,0)=Y vert, (0,0,1)=Z bleu.")]
    public Vector3 localAxis = Vector3.up;
    [Tooltip("À cocher si la jauge ne monte pas quand on tourne dans le sens horaire.")]
    public bool invertDirection = false;

    [Header("Affichage")]
    [Tooltip("L'écrou tourne visuellement sur lui-même pendant le serrage.")]
    public bool rotateVisually = true;
    [Tooltip("Optionnel : texte TextMeshPro pour afficher la jauge (couple / angle).")]
    public TMP_Text display;

    [Header("Réactions (vibration, texte, son... à câbler à l'étape feedback)")]
    public UnityEvent onTorqueWindowEntered;  // le « clic » de la clé dynamométrique
    public UnityEvent onTorqueValidated;
    public UnityEvent onAngleValidated;
    public UnityEvent onOverTightened;        // sur-serrage (couple ou angle)
    public UnityEvent onUnderTightened;       // clé réglée trop bas : couple insuffisant
    public UnityEvent onOutOfSequence;        // angle commencé avant le pré-serrage de toutes les vis

    public Phase CurrentPhase { get; private set; } = Phase.Inactif;
    public float CurrentTorque { get; private set; }
    public float CurrentAngle { get; private set; }
    public bool HadError { get; private set; }

    [HideInInspector] public TighteningGroup group;

    private bool clickDone;
    private bool sequenceWarned;
    private float degreesAfterClick;
    private bool overWarned;
    private bool angleReachedDone;
    private TorqueSetting setting;            // réglage de la clé engagée (null = clé non réglable)

    // Valeur à laquelle la clé clique : celle réglée sur la clé, sinon la consigne de l'écrou.
    private float ClickTorque => setting != null ? setting.value : targetTorque;

    public Vector3 WorldAxis => transform.TransformDirection(localAxis).normalized;
    public bool IsActive => CurrentPhase == Phase.Couple || CurrentPhase == Phase.Angle;

    // --- Interface « cible d'outil » ---
    public override bool AcceptsTool => IsActive;
    public override Vector3 ToolAxis => WorldAxis;
    public override void OnToolEngaged(Tool tool) => setting = tool.GetComponent<TorqueSetting>();
    public override void OnToolRotate(float degrees) => AddRotation(degrees);
    public override void OnToolDisengaged() => Release();

    void Awake()
    {
        if (!autoDetectAxis) return;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("[Serrage] " + name + " : pas de maillage sur l'objet, détection d'axe impossible. Réglez Local Axis à la main.");
            return;
        }

        Vector3 s = mf.sharedMesh.bounds.size;
        if (axisShape == AxisShape.Ecrou)
        {
            // Un écrou est plus fin dans le sens de la vis.
            if (s.x <= s.y && s.x <= s.z) localAxis = Vector3.right;
            else if (s.y <= s.x && s.y <= s.z) localAxis = Vector3.up;
            else localAxis = Vector3.forward;
        }
        else
        {
            // Une vis est plus longue dans le sens de son axe.
            if (s.x >= s.y && s.x >= s.z) localAxis = Vector3.right;
            else if (s.y >= s.x && s.y >= s.z) localAxis = Vector3.up;
            else localAxis = Vector3.forward;
        }

        Log("axe de vis détecté : " + localAxis);
    }

    public void Activate()
    {
        if (CurrentPhase != Phase.Inactif) return;
        CurrentPhase = Phase.Couple;
        Log("activé, prêt pour le serrage au couple.");
        UpdateDisplay();
    }

    // Appelé chaque frame par la clé quand elle est engagée sur cet écrou.
    public void AddRotation(float degrees)
    {
        float toolDegrees = degrees;            // rotation réelle de la clé (pour l'animation)
        if (invertDirection) degrees = -degrees;
        if (degrees <= 0f) return; // effet cliquet

        switch (CurrentPhase)
        {
            case Phase.Couple:
                float rate = targetTorque / degreesToReachTorque;
                if (!clickDone)
                {
                    CurrentTorque = Mathf.Min(CurrentTorque + degrees * rate, ClickTorque);
                    if (CurrentTorque >= ClickTorque)
                    {
                        clickDone = true;
                        Log("clic à " + ClickTorque.ToString("F0") + " N·m, relâchez.");
                        Pulse(1f, 0.15f);                     // le « clic » ressenti dans la main
                        onTorqueWindowEntered.Invoke();
                    }
                }
                else
                {
                    // La clé a décroché : le couple ne monte presque plus, mais on mesure l'insistance.
                    degreesAfterClick += degrees;
                    CurrentTorque += degrees * rate * postClickGain;
                    if (!overWarned && degreesAfterClick > maxDegreesAfterClick)
                    {
                        overWarned = true;
                        Log("attention : vous forcez après le clic (sur-serrage).");
                        StartCoroutine(DoublePulse());
                    }
                }
                break;

            case Phase.Angle:
                if (group != null && !group.AllTorqued())
                {
                    if (!sequenceWarned)
                    {
                        sequenceWarned = true;
                        HadError = true;
                        Log("HORS SEQUENCE : pré-serrer toutes les vis au couple avant le serrage angulaire.");
                        onOutOfSequence.Invoke();
                    }
                    if (!group.modeEvaluation) return; // mode Apprentissage : on bloque
                }
                CurrentAngle += degrees;
                if (!angleReachedDone && CurrentAngle >= targetAngle - angleTolerance)
                {
                    angleReachedDone = true;
                    Log("angle cible atteint, relâchez.");
                    Pulse(1f, 0.15f);
                }
                else if (angleReachedDone && !overWarned && CurrentAngle > targetAngle + angleTolerance)
                {
                    overWarned = true;
                    Log("attention : angle dépassé (sur-serrage).");
                    StartCoroutine(DoublePulse());
                }
                break;

            default:
                return;
        }

        // L'écrou suit la clé : il tourne sur lui-même autour de l'axe de la vis.
        if (rotateVisually) transform.Rotate(localAxis, toolDegrees, Space.Self);

        UpdateDisplay();
    }

    // Appelé par la clé quand elle se désengage (relâchement ou éloignement) : on juge la valeur.
    public void Release()
    {
        sequenceWarned = false;

        switch (CurrentPhase)
        {
            case Phase.Couple:
                if (!clickDone)
                {
                    if (CurrentTorque > 0f)
                        Log("couple insuffisant (" + CurrentTorque.ToString("F1") + " N·m), continuez jusqu'au clic.");
                    break;
                }

                if (CurrentTorque < targetTorque - torqueTolerance)
                {
                    // La clé a cliqué trop tôt : elle était réglée trop bas.
                    HadError = true;
                    Log("COUPLE INSUFFISANT : clé réglée à " + ClickTorque.ToString("F0") + " N·m au lieu de "
                        + targetTorque.ToString("F0") + " N·m. Réglez la clé et reprenez.");
                    onUnderTightened.Invoke();
                    clickDone = false;            // on peut reprendre après avoir réglé la clé
                    degreesAfterClick = 0f;
                    overWarned = false;
                    break;
                }

                if (CurrentTorque > targetTorque + torqueTolerance)
                {
                    HadError = true;
                    Log("SUR-SERRAGE : clé réglée à " + ClickTorque.ToString("F0") + " N·m au lieu de "
                        + targetTorque.ToString("F0") + " N·m.");
                    onOverTightened.Invoke();
                }
                else if (degreesAfterClick > maxDegreesAfterClick)
                {
                    HadError = true;
                    Log("SUR-SERRAGE au couple (" + degreesAfterClick.ToString("F0") + "° forcés après le clic).");
                    onOverTightened.Invoke();
                }
                else Log("couple validé (" + CurrentTorque.ToString("F1") + " N·m).");

                CurrentPhase = Phase.Angle;
                overWarned = false;   // on repart à zéro pour la phase angulaire
                onTorqueValidated.Invoke();
                if (group != null) group.NotifyChanged();
                break;

            case Phase.Angle:
                if (CurrentAngle < targetAngle - angleTolerance)
                {
                    if (CurrentAngle > 0f)
                        Log("angle insuffisant (" + CurrentAngle.ToString("F0") + " / " + targetAngle.ToString("F0") + "°), continuez.");
                }
                else
                {
                    if (CurrentAngle > targetAngle + angleTolerance)
                    {
                        HadError = true;
                        Log("SUR-SERRAGE angulaire (" + CurrentAngle.ToString("F0") + "°).");
                        onOverTightened.Invoke();
                    }
                    else Log("angle validé (" + CurrentAngle.ToString("F0") + "°).");

                    CurrentPhase = Phase.Termine;
                    onAngleValidated.Invoke();
                    if (group != null) group.NotifyChanged();
                }
                break;
        }
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (display == null) return;
        switch (CurrentPhase)
        {
            case Phase.Couple: display.text = "Couple\n" + CurrentTorque.ToString("F1") + " / " + targetTorque.ToString("F0") + " N·m"; break;
            case Phase.Angle: display.text = "Angle\n" + CurrentAngle.ToString("F0") + " / " + targetAngle.ToString("F0") + "°"; break;
            case Phase.Termine: display.text = HadError ? "Serré (erreur)" : "Serré OK"; break;
            default: display.text = ""; break;
        }
    }

    // ---------- Vibrations ----------

    private void Pulse(float amplitude, float duration)
    {
        if (haptics) StartCoroutine(PulseRoutine(amplitude, duration));
    }

    private IEnumerator PulseRoutine(float amplitude, float duration)
    {
        OVRInput.SetControllerVibration(1f, amplitude, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(1f, amplitude, OVRInput.Controller.LTouch);
        yield return new WaitForSeconds(duration);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
    }

    // Deux vibrations rapprochées = alerte (sur-serrage).
    private IEnumerator DoublePulse()
    {
        if (!haptics) yield break;
        yield return PulseRoutine(1f, 0.1f);
        yield return new WaitForSeconds(0.08f);
        yield return PulseRoutine(1f, 0.1f);
    }

    private void Log(string msg) => Debug.Log("[Serrage] " + name + " : " + msg);
}