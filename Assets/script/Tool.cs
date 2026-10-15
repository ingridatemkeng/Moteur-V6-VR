using System.Collections;
using UnityEngine;
using UnityEngine.Events;

// À placer sur chaque outil attrapable (clé dynamométrique, tournevis...).
// - Quand le bout de l'outil approche une cible active :
//     bon outil     -> il s'engage ;
//     mauvais outil -> message « mauvais outil » (erreur pédagogique).
// - Deux façons de faire tourner la cible :
//     Levier  (clé) : l'outil se cale sur l'écrou et pivote autour de l'axe de la vis ;
//                     c'est la position de la main autour de l'écrou qui le fait tourner (geste réel).
//     Torsion (tournevis, clé Allen) : c'est la rotation du poignet autour de l'axe qui compte.
// - Relâché, l'outil revient tout seul à sa place (position de départ).
// L'outil doit être enfant de « Poste » pour suivre le placement du poste.
//
// Câblage (PointableUnityEventWrapper de l'outil) :
//   When Select   -> Tool.OnGrab()
//   When Unselect -> Tool.OnRelease()

[DefaultExecutionOrder(10000)]   // s'exécute après l'Interaction SDK, pour pouvoir caler l'outil sur l'écrou
public class Tool : MonoBehaviour
{
    public enum DriveMode { Levier, Torsion }

    [Header("Outil")]
    public ToolType type = ToolType.CleDynamometrique;
    [Tooltip("Levier : clé (on fait pivoter le manche autour de l'écrou). Torsion : tournevis, clé Allen (on tourne le poignet).")]
    public DriveMode driveMode = DriveMode.Levier;
    [Tooltip("Point de travail de l'outil (carré d'entraînement, pointe du tournevis...). Vide = l'origine de l'objet.")]
    public Transform tip;
    [Tooltip("Distance (m) à laquelle l'outil s'engage sur une cible.")]
    public float engageDistance = 0.05f;

    [Header("Mode Levier")]
    [Tooltip("Distance (m) dont il faut écarter la main de l'écrou pour décrocher la clé.")]
    public float pullOffDistance = 0.10f;

    [Header("Diagnostic")]
    [Tooltip("Affiche chaque seconde la distance à la cible la plus proche (à décocher une fois réglé).")]
    public bool debugDistances = true;

    [Header("Retour à sa place")]
    public bool returnHomeOnRelease = true;
    public float returnDuration = 0.4f;

    [Header("Réactions (vibration, son... à câbler à l'étape feedback)")]
    public UnityEvent onEngaged;
    public UnityEvent onWrongTool;

    public bool IsHeld => held;

    private ToolTarget[] targets;
    private ToolTarget engaged;
    private ToolTarget warnedTarget;   // pour n'afficher « mauvais outil » qu'une fois par approche
    private bool held;

    // Mode Torsion
    private Quaternion lastRotation;

    // Mode Levier
    private Vector3 localHandle;       // point du manche (centre de l'outil), dans le repère de l'outil
    private Quaternion engageRotation;
    private Vector3 lastLever;
    private float accumulatedAngle;

    private Vector3 homeLocalPos;
    private Quaternion homeLocalRot;
    private Coroutine returning;
    private float nextDebugTime;

    private Vector3 TipPos => tip != null ? tip.position : transform.position;

    void Awake()
    {
        // La position de départ (dans le repère du parent, donc du poste) devient « sa place ».
        homeLocalPos = transform.localPosition;
        homeLocalRot = transform.localRotation;

        // Le « manche » = centre visible de l'outil : c'est lui qu'on suit autour de l'écrou.
        Renderer r = GetComponentInChildren<Renderer>();
        localHandle = r != null ? transform.InverseTransformPoint(r.bounds.center) : Vector3.zero;
    }

    public void OnGrab()
    {
        held = true;
        if (returning != null) { StopCoroutine(returning); returning = null; }
        // Les cibles masquées (écrous pas encore disponibles) ne sont pas trouvées : c'est voulu.
        targets = FindObjectsByType<ToolTarget>(FindObjectsSortMode.None);
        warnedTarget = null;

        int ready = 0;
        foreach (var t in targets) if (t != null && t.AcceptsTool) ready++;
        Log("pris en main. Cibles trouvées : " + targets.Length + ", prêtes : " + ready + ".");
        if (targets.Length == 0) Log("aucune cible : les écrous sont masqués (chapeau pas encore posé ?) ou n'ont pas le composant Nut Tightening.");
        else if (ready == 0) Log("aucune cible prête : posez d'abord le chapeau de bielle.");
    }

    public void OnRelease()
    {
        held = false;
        Disengage();
        if (returnHomeOnRelease) returning = StartCoroutine(ReturnHome());
    }

    void Update()
    {
        if (!held) return;

        if (engaged == null) { DebugNearest(); TryEngage(); return; }

        if (driveMode == DriveMode.Torsion) UpdateTorsion();
        // Le mode Levier est traité dans LateUpdate, après que l'Interaction SDK a placé l'outil dans la main.
    }

    void LateUpdate()
    {
        if (!held || engaged == null || driveMode != DriveMode.Levier) return;

        Vector3 axis = engaged.ToolAxis.normalized;
        Vector3 center = engaged.WorkPoint;

        // Pose « voulue par la main » ce frame (avant qu'on cale l'outil sur l'écrou).
        Vector3 handTip = TipPos;
        Vector3 handHandle = transform.TransformPoint(localHandle);

        // Main écartée de l'écrou, ou cible terminée -> on décroche.
        if (!engaged.AcceptsTool || Vector3.Distance(handTip, center) > pullOffDistance)
        {
            Disengage();
            return;
        }

        // Angle du manche autour de l'axe de la vis.
        Vector3 lever = Vector3.ProjectOnPlane(handHandle - center, axis);
        if (lever.sqrMagnitude > 1e-6f)
        {
            if (lastLever.sqrMagnitude > 1e-6f)
            {
                float delta = Vector3.SignedAngle(lastLever, lever, axis);
                if (Mathf.Abs(delta) > 0.01f)
                {
                    accumulatedAngle += delta;
                    engaged.OnToolRotate(delta);   // l'écrou applique lui-même l'effet cliquet
                }
            }
            lastLever = lever;
        }

        // On cale l'outil : carré d'entraînement sur l'écrou, rotation uniquement autour de l'axe.
        transform.rotation = Quaternion.AngleAxis(accumulatedAngle, axis) * engageRotation;
        transform.position += center - TipPos;
    }

    private void UpdateTorsion()
    {
        if (!engaged.AcceptsTool || Vector3.Distance(TipPos, engaged.WorkPoint) > engageDistance * 2f)
        {
            Disengage();
            return;
        }

        Quaternion current = transform.rotation;
        Quaternion delta = current * Quaternion.Inverse(lastRotation);
        lastRotation = current;

        float degrees = TwistAngle(delta, engaged.ToolAxis.normalized);
        if (Mathf.Abs(degrees) > 0.01f) engaged.OnToolRotate(degrees);
    }

    private void TryEngage()
    {
        if (targets == null) return;

        ToolTarget nearest = null;
        float best = engageDistance;
        foreach (var t in targets)
        {
            if (t == null || !t.isActiveAndEnabled || !t.AcceptsTool) continue;
            float d = Vector3.Distance(TipPos, t.WorkPoint);
            if (d <= best) { best = d; nearest = t; }
        }

        if (nearest == null)
        {
            // On s'est éloigné de la cible signalée : un nouveau passage pourra re-déclencher le message.
            if (warnedTarget != null && Vector3.Distance(TipPos, warnedTarget.WorkPoint) > engageDistance * 2f)
                warnedTarget = null;
            return;
        }

        if (nearest.requiredTool != type)
        {
            if (warnedTarget != nearest)
            {
                warnedTarget = nearest;
                Debug.Log("[Outil] mauvais outil sur " + nearest.name + " : " + ToolNames.Of(type)
                          + " utilisé, il faut " + ToolNames.WithArticle(nearest.requiredTool) + ".");
                onWrongTool.Invoke();
            }
            return;
        }

        engaged = nearest;
        lastRotation = transform.rotation;
        engageRotation = transform.rotation;
        accumulatedAngle = 0f;
        lastLever = Vector3.ProjectOnPlane(transform.TransformPoint(localHandle) - nearest.WorkPoint, nearest.ToolAxis.normalized);

        engaged.OnToolEngaged(this);
        onEngaged.Invoke();
        Log("engagé sur " + nearest.name + ".");
    }

    private void Disengage()
    {
        if (engaged == null) return;
        ToolTarget t = engaged;
        engaged = null;
        t.OnToolDisengaged();
        Log("désengagé de " + t.name + ".");
    }

    private void DebugNearest()
    {
        if (!debugDistances || targets == null || Time.time < nextDebugTime) return;
        nextDebugTime = Time.time + 1f;

        ToolTarget nearest = null;
        float best = float.MaxValue;
        foreach (var t in targets)
        {
            if (t == null || !t.isActiveAndEnabled) continue;
            float d = Vector3.Distance(TipPos, t.WorkPoint);
            if (d < best) { best = d; nearest = t; }
        }
        if (nearest != null)
            Log("cible la plus proche : " + nearest.name + " à " + (best * 100f).ToString("F0")
                + " cm (seuil " + (engageDistance * 100f).ToString("F0") + " cm, prête : " + (nearest.AcceptsTool ? "oui" : "non") + ").");
    }

    private IEnumerator ReturnHome()
    {
        Vector3 startPos = transform.localPosition;
        Quaternion startRot = transform.localRotation;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.01f, returnDuration);
            float s = Mathf.SmoothStep(0f, 1f, t);
            transform.localPosition = Vector3.Lerp(startPos, homeLocalPos, s);
            transform.localRotation = Quaternion.Slerp(startRot, homeLocalRot, s);
            yield return null;
        }
        returning = null;
    }

    // Angle signé (degrés) de la rotation q autour de l'axe donné (décomposition swing-twist).
    private static float TwistAngle(Quaternion q, Vector3 axis)
    {
        float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
        float angle = 2f * Mathf.Atan2(proj, q.w) * Mathf.Rad2Deg;
        if (angle > 180f) angle -= 360f;
        else if (angle < -180f) angle += 360f;
        return angle;
    }

    private void Log(string msg) => Debug.Log("[Outil] " + ToolNames.Of(type) + " : " + msg);
}