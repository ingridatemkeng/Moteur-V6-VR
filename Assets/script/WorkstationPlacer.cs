using UnityEngine;
using UnityEngine.Events;
using TMPro;

// Démarrage de l'expérience en réalité mixte :
// 1. Attente      : seul l'environnement réel est visible, un message invite à appuyer sur A.
// 2. Positionnement : le poste apparaît et suit la manette droite ; on la pose sur une surface, A.
// 3. Ajustement   : le joystick tourne le poste ; A valide, B recommence.
// 4. Verrouillé   : l'exercice peut commencer ; B permet de replacer le poste.
// Tout le poste (moteur, pièces, cibles, clé) se déplace ensemble.

public class WorkstationPlacer : MonoBehaviour
{
    public enum State { Attente, Positionnement, Ajustement, Verrouille }

    [Header("Références")]
    [Tooltip("Objet racine qui contient tout l'exercice. Rotation (0,0,0), échelle 1.")]
    public Transform poste;
    [Tooltip("Le Mesh Renderer du bloc moteur : son dessous sera posé sur la surface.")]
    public Renderer baseRenderer;
    [Tooltip("[BuildingBlock] Camera Rig > TrackingSpace > RightHandAnchor > RightControllerAnchor")]
    public Transform controllerAnchor;
    [Tooltip("[BuildingBlock] Camera Rig > TrackingSpace > CenterEyeAnchor")]
    public Transform head;

    [Header("Contenu masqué au démarrage")]
    [Tooltip("Objets cachés jusqu'à l'appui sur A. Laisser vide = tous les enfants du poste.")]
    public GameObject[] content;

    [Header("Réglages du placement")]
    [Tooltip("Correction de hauteur (m) : le repère de la manette est un peu au-dessus de la table quand elle est posée.")]
    public float heightOffset = -0.03f;
    [Tooltip("Vitesse de rotation au joystick (degrés/seconde).")]
    public float rotationSpeed = 90f;
    [Tooltip("Rotation ajoutée pour choisir quelle face du moteur est tournée vers l'utilisateur (0, 90, 180, 270).")]
    public float initialYawOffset = 0f;
    public OVRInput.Controller controller = OVRInput.Controller.RTouch;

    [Header("Panneau de consignes (texte TextMeshPro 3D, PAS enfant du poste)")]
    public TMP_Text instructions;
    [Tooltip("Distance du panneau devant les yeux (m).")]
    public float panelDistance = 0.8f;
    [Tooltip("Le panneau est placé un peu sous le regard (m).")]
    public float panelDrop = 0.1f;
    [Tooltip("Vitesse à laquelle le panneau rattrape le regard (plus grand = plus rapide).")]
    public float panelFollowSpeed = 3f;

    [Header("Réactions")]
    public UnityEvent onPlacementConfirmed;   // ex. démarrer l'exercice, lancer l'enregistrement

    public State CurrentState { get; private set; }

    private Vector3 localBottomCenter;  // dessous du bloc, exprimé dans le repère du poste
    private Vector3 placedPoint;
    private float yaw;
    private bool panelInitialized;

    void Awake()
    {
        if (poste == null) poste = transform;

        // 1) On mesure le dessous du bloc AVANT de masquer quoi que ce soit
        //    (un objet masqué a une boîte englobante vide).
        if (baseRenderer != null)
        {
            Bounds b = baseRenderer.bounds;
            localBottomCenter = poste.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z));
        }
        else
        {
            Debug.LogWarning("[Placement] Base Renderer non renseigné : le poste sera placé par son origine.");
            localBottomCenter = Vector3.zero;
        }

        // 2) Liste du contenu à masquer : par défaut, tous les enfants du poste.
        if (content == null || content.Length == 0)
        {
            content = new GameObject[poste.childCount];
            for (int i = 0; i < poste.childCount; i++) content[i] = poste.GetChild(i).gameObject;
        }

        // 3) Au lancement, on ne voit que l'environnement réel.
        SetContentVisible(false);
        EnterWaiting();
    }

    void Update()
    {
        if (controllerAnchor == null || head == null) return;

        FollowPanel();

        bool pressA = OVRInput.GetDown(OVRInput.Button.One, controller);
        bool pressB = OVRInput.GetDown(OVRInput.Button.Two, controller);

        switch (CurrentState)
        {
            case State.Attente:
                if (pressA)
                {
                    // On place le poste sur la manette AVANT de l'afficher,
                    // pour éviter qu'il apparaisse une image à son ancienne position.
                    UpdatePositioningPose();
                    SetContentVisible(true);
                    EnterPositioning();
                }
                break;

            case State.Positionnement:
                UpdatePositioningPose();
                if (pressA)
                {
                    CurrentState = State.Ajustement;
                    SetText("Joystick : tourner le moteur\nA : valider    B : recommencer");
                    Debug.Log("[Placement] position choisie, ajustez la rotation puis validez avec A.");
                }
                break;

            case State.Ajustement:
                float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller).x;
                if (Mathf.Abs(x) > 0.2f)
                {
                    yaw += x * rotationSpeed * Time.deltaTime;
                    Apply();
                }

                if (pressA) Lock();
                else if (pressB) EnterPositioning();
                break;

            case State.Verrouille:
                if (pressB) EnterPositioning();
                break;
        }
    }

    // ---------- États ----------

    private void EnterWaiting()
    {
        CurrentState = State.Attente;
        SetText("Bienvenue\nAppuyez sur A pour faire apparaître\nle poste de montage");
        Debug.Log("[Placement] attente : appuyez sur A pour faire apparaître le poste.");
    }

    private void EnterPositioning()
    {
        CurrentState = State.Positionnement;
        SetText("Posez la manette droite sur la table\npuis appuyez sur A");
        Debug.Log("[Placement] mode positionnement : posez la manette droite sur une surface et appuyez sur A.");
    }

    private void Lock()
    {
        CurrentState = State.Verrouille;
        SetText("");
        Debug.Log("[Placement] poste verrouillé. B pour le replacer.");
        onPlacementConfirmed.Invoke();
    }

    // ---------- Placement ----------

    private void UpdatePositioningPose()
    {
        placedPoint = controllerAnchor.position + Vector3.up * heightOffset;
        yaw = YawTowardHead(placedPoint) + initialYawOffset;
        Apply();
    }

    // Oriente le poste face à l'utilisateur (rotation autour de la verticale uniquement)
    // et pose le dessous du bloc exactement sur le point choisi.
    private void Apply()
    {
        poste.rotation = Quaternion.Euler(0f, yaw, 0f);
        poste.position = placedPoint - poste.TransformVector(localBottomCenter);
    }

    private float YawTowardHead(Vector3 point)
    {
        Vector3 d = head.position - point;
        d.y = 0f;
        if (d.sqrMagnitude < 0.0001f) return yaw;
        return Quaternion.LookRotation(d).eulerAngles.y;
    }

    private void SetContentVisible(bool visible)
    {
        foreach (var go in content) if (go != null) go.SetActive(visible);
    }

    // ---------- Panneau de consignes ----------

    // Le panneau suit le regard en douceur (« lazy follow ») : il reste devant l'utilisateur
    // sans être collé à sa tête, ce qui est plus confortable en casque.
    private void FollowPanel()
    {
        if (instructions == null) return;
        Transform panel = instructions.transform;

        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 targetPos = head.position + forward * panelDistance + Vector3.down * panelDrop;

        if (!panelInitialized)
        {
            panel.position = targetPos;   // première image : on place le panneau directement
            panelInitialized = true;
        }
        else
        {
            float t = 1f - Mathf.Exp(-panelFollowSpeed * Time.deltaTime);
            panel.position = Vector3.Lerp(panel.position, targetPos, t);
        }

        // Le texte TextMeshPro se lit en regardant dans la direction de son axe Z :
        // on l'oriente donc dans la direction « tête -> panneau ».
        Vector3 look = panel.position - head.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.0001f) panel.rotation = Quaternion.LookRotation(look);
    }

    private void SetText(string msg)
    {
        if (instructions != null) instructions.text = msg;
    }
}