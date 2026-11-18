using System.Collections;
using UnityEngine;
using UnityEngine.Events;

// Support moteur rotatif : retourne le bloc de 180° autour de l'axe du vilebrequin.
// Comme en atelier : le bas moteur (Gamme 1) se monte bloc retourné, puis on fait pivoter
// le bloc pour monter le haut moteur (Gamme 2).
// - Disponible quand la Gamme 1 est terminée (ou tout de suite si aucun groupe n'est renseigné).
// - Bouton Y (manette gauche) : retourner ; un nouvel appui le remet dans l'autre sens.
// - Toutes les pièces déjà montées tournent avec le bloc (elles sont enfants du moteur).
//
// À placer sur un objet de la scène (ex. Poste). Engine = gamme1, Block Renderer = le bloc moteur.

public class EngineFlipper : MonoBehaviour
{
    [Header("Références")]
    [Tooltip("L'objet qui contient tout le moteur et ses pièces (gamme1).")]
    public Transform engine;
    [Tooltip("Le Mesh Renderer du bloc moteur : sert à trouver l'axe du vilebrequin et le dessous du bloc.")]
    public Renderer blockRenderer;
    [Tooltip("Le groupe de serrage de la Gamme 1 : le retournement devient possible quand il est terminé. Vide = toujours possible.")]
    public TighteningGroup requiredGroup;
    public WorkstationPlacer placer;

    [Header("Commande")]
    public OVRInput.Button flipButton = OVRInput.Button.Four;          // Y
    public OVRInput.Controller controller = OVRInput.Controller.LTouch;

    [Header("Mouvement")]
    [Tooltip("Durée du retournement (s).")]
    public float duration = 2.5f;
    [Tooltip("Axe de rotation dans le repère LOCAL du bloc. (0,0,0) = trouvé automatiquement (la plus grande longueur du bloc, c'est-à-dire l'axe du vilebrequin).")]
    public Vector3 localFlipAxis = Vector3.zero;

    public UnityEvent onFlipAvailable;
    public UnityEvent onFlipped;

    public bool IsFlipped { get; private set; }
    public bool IsAvailable => available;

    private bool available;
    private bool flipping;

    void Start()
    {
        if (placer == null) placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);
        Debug.Log("[Guide] support rotatif prêt : moteur " + (engine != null ? engine.name : "NON RENSEIGNÉ")
                  + ", bloc " + (blockRenderer != null ? blockRenderer.name : "NON RENSEIGNÉ")
                  + (requiredGroup != null ? ", disponible après la Gamme 1." : ", disponible tout de suite."));

        if (requiredGroup != null) requiredGroup.onAllTightened.AddListener(MakeAvailable);
        else MakeAvailable();
    }

    private void MakeAvailable()
    {
        if (available) return;
        available = true;
        Debug.Log("[Guide] le moteur peut être retourné : appuyez sur Y.");
        onFlipAvailable.Invoke();
    }

    void Update()
    {
        // Bouton Y : lu de deux façons (bouton « logique » de la manette gauche, et bouton physique Y).
        bool pressed = OVRInput.GetDown(flipButton, controller) || OVRInput.GetDown(OVRInput.RawButton.Y);
        if (!pressed || flipping) return;
        Debug.Log("[Guide] bouton Y reçu.");

        // On explique toujours pourquoi le retournement est refusé.
        if (engine == null || blockRenderer == null)
            Debug.LogWarning("[Guide] retournement impossible : renseignez Engine (gamme1) et Block Renderer (block_moteur) sur Engine Flipper.");
        else if (placer != null && placer.CurrentState != WorkstationPlacer.State.Verrouille)
            Debug.Log("[Guide] retournement impossible : validez d'abord le placement du poste (A).");
        else if (!available)
            Debug.Log("[Guide] retournement pas encore disponible : terminez d'abord le serrage de la Gamme 1"
                      + " (ou videz Required Group pour tester).");
        else
            StartCoroutine(Flip());
    }

    // Axe du vilebrequin en coordonnées monde : la plus grande dimension du bloc.
    private Vector3 WorldFlipAxis()
    {
        Vector3 local = localFlipAxis;
        if (local == Vector3.zero)
        {
            MeshFilter mf = blockRenderer.GetComponent<MeshFilter>();
            Vector3 s = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.bounds.size : Vector3.one;
            if (s.x >= s.y && s.x >= s.z) local = Vector3.right;
            else if (s.y >= s.x && s.y >= s.z) local = Vector3.up;
            else local = Vector3.forward;
        }
        return blockRenderer.transform.TransformDirection(local).normalized;
    }

    private IEnumerator Flip()
    {
        flipping = true;
        Debug.Log("[Guide] retournement du moteur...");

        Bounds start = blockRenderer.bounds;
        Vector3 pivot = start.center;
        Vector3 axis = WorldFlipAxis();
        float groundY = start.min.y;
        // On soulève le bloc pendant la rotation pour que ses coins ne traversent pas la table.
        float lift = start.extents.magnitude - start.extents.y;

        float done = 0f;       // angle déjà appliqué
        float lifted = 0f;     // hauteur déjà appliquée
        float t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / Mathf.Max(0.1f, duration));
            float s = Mathf.SmoothStep(0f, 1f, t);

            float angle = 180f * s;
            engine.RotateAround(pivot, axis, angle - done);
            done = angle;

            float h = Mathf.Sin(Mathf.PI * s) * lift;
            engine.position += Vector3.up * (h - lifted);
            pivot += Vector3.up * (h - lifted);
            lifted = h;

            yield return null;
        }

        // Le nouveau dessous du bloc se repose exactement à la hauteur de la table.
        float newMin = blockRenderer.bounds.min.y;
        engine.position += Vector3.up * (groundY - newMin);
        if (placer != null) placer.RecomputeBottom();

        IsFlipped = !IsFlipped;
        flipping = false;
        Debug.Log("[Guide] moteur retourné (" + (IsFlipped ? "côté haut moteur" : "côté bas moteur") + ").");
        onFlipped.Invoke();
    }
}
