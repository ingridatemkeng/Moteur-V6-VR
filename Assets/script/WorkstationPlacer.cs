using UnityEngine;
using UnityEngine.Events;
using TMPro;

// Phase de placement au démarrage : l'utilisateur pose la manette droite sur une surface reelle
// (table, etabli, sol), appuie sur A, ajuste la rotation au joystick, puis valide avec A.
// Tout le poste (moteur, pièces, cibles, cle) se déplace ensemble.
// B permet de replacer le poste à tout moment.

public class WorkstationPlacer : MonoBehaviour
{
    public enum State { Positionnement, Ajustement, Verrouille }

    [Header("Références")]
    [Tooltip("Objet racine qui contient tout l'exercice (gamme1, cle...). Rotation (0,0,0), échelle 1.")]
    public Transform poste;
    [Tooltip("Le Mesh Renderer du bloc moteur : son dessous sera pose sur la surface.")]
    public Renderer baseRenderer;
    [Tooltip("[BuildingBlock] Camera Rig > TrackingSpace > RightHandAnchor > RightControllerAnchor")]
    public Transform controllerAnchor;
    [Tooltip("[BuildingBlock] Camera Rig > TrackingSpace > CenterEyeAnchor")]
    public Transform head;

    [Header("Réglages")]
    [Tooltip("Correction de hauteur (m) : le repère de la manette est un peu au-dessus de la table quand elle est posée.")]
    public float heightOffset = -0.03f;
    [Tooltip("Vitesse de rotation au joystick (degrés/seconde).")]
    public float rotationSpeed = 90f;
    [Tooltip("Rotation ajoutée pour choisir quelle face du moteur est tournée vers l'utilisateur (0, 90, 180, 270).")]
    public float initialYawOffset = 0f;
    public OVRInput.Controller controller = OVRInput.Controller.RTouch;

    [Header("Affichage (optionnel)")]
    public TMP_Text instructions;

    [Header("Réactions")]
    public UnityEvent onPlacementConfirmed;   //on démarre l'exercice, lancer l'enregistrement

    public State CurrentState { get; private set; } 

    private Vector3 localBottomCenter;  // dessous du bloc, exprimé dans le repère du poste
    private Vector3 placedPoint;
    private float yaw;

    void Start()
    {
        if (poste == null) poste = transform;

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

        EnterPositioning();
    }

    void Update()
    {
        if (controllerAnchor == null || head == null) return;

        bool pressA = OVRInput.GetDown(OVRInput.Button.One, controller);
        bool pressB = OVRInput.GetDown(OVRInput.Button.Two, controller);

        switch (CurrentState)
        {
            case State.Positionnement:
                // Le poste suit la manette en temps réel.
                placedPoint = controllerAnchor.position + Vector3.up * heightOffset;
                yaw = YawTowardHead(placedPoint) + initialYawOffset;
                Apply();

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

    private void SetText(string msg)
    {
        if (instructions != null) instructions.text = msg;
    }
}