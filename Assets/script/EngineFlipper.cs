using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

// Support moteur rotatif : retourne le bloc de 180° autour de l'axe du vilebrequin.
// Comme en atelier (gamme, opération 20) :
// - au début, le bloc apparaît À L'ENDROIT (comme dans la voiture) ;
// - on le retourne (plan de carter vers le haut) pour monter le bas moteur ;
// - on peut le retourner à tout moment (bouton Y).
// Le bloc, les pièces DÉJÀ POSÉES et les fantômes tournent ensemble.
// Les pièces encore sur la table ne bougent pas.
//
// Dans la scène Unity, le moteur est modélisé carter vers le haut (position de montage du bas moteur).
// Si « Start Upright » est coché, il est retourné une fois, instantanément, au lancement.
//
// À placer sur un objet de la scène (ex. Poste). Engine = gamme1, Block Renderer = le bloc moteur.

[DefaultExecutionOrder(-50)]   // avant le WorkstationPlacer, qui mesure le dessous du bloc dans son Awake
public class EngineFlipper : MonoBehaviour
{
    public static EngineFlipper Instance { get; private set; }

    [Header("Références")]
    [Tooltip("L'objet qui contient tout le moteur et ses pièces (gamme1).")]
    public Transform engine;
    [Tooltip("Le Mesh Renderer du bloc moteur : sert à trouver l'axe du vilebrequin et le dessous du bloc.")]
    public Renderer blockRenderer;
    [Tooltip("Optionnel : retournement possible seulement après ce serrage. VIDE = possible à tout moment (recommandé).")]
    public TighteningGroup requiredGroup;
    public WorkstationPlacer placer;

    [Header("Départ")]
    [Tooltip("Coché : le moteur apparaît à l'endroit (comme dans la voiture). Il faut le retourner pour monter le bas moteur.")]
    public bool startUpright = true;

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

    // Vrai quand le bloc est carter vers le haut (position de montage du bas moteur, comme dans la scène Unity).
    public bool IsUpsideDown { get; private set; } = true;
    // Vrai quand le bloc n'est plus dans sa position de départ.
    public bool IsFlipped => startUpright ? IsUpsideDown : !IsUpsideDown;
    public bool IsAvailable => available;
    public bool IsFlipping => flipping;

    private bool available;
    private bool flipping;

    void Awake()
    {
        Instance = this;
        if (startUpright && engine != null && blockRenderer != null)
        {
            // Retournement instantané, avant que le poste ne masque son contenu (un objet masqué n'a plus de taille).
            Bounds b = blockRenderer.bounds;
            var loose = DetachLoosePieces();
            engine.RotateAround(b.center, WorldFlipAxis(), 180f);
            engine.position += Vector3.up * (b.min.y - blockRenderer.bounds.min.y);
            Reattach(loose);
            IsUpsideDown = false;
        }
    }

    void Start()
    {
        if (placer == null) placer = FindFirstObjectByType<WorkstationPlacer>(FindObjectsInactive.Include);

        Debug.Log("[Guide] support rotatif prêt : moteur " + (engine != null ? engine.name : "NON RENSEIGNÉ")
                  + ", bloc " + (blockRenderer != null ? blockRenderer.name : "NON RENSEIGNÉ")
                  + (IsUpsideDown ? ", carter vers le haut" : ", à l'endroit")
                  + (requiredGroup != null ? ", retournement après la Gamme 1." : ", retournement possible à tout moment (Y)."));

        if (requiredGroup != null) requiredGroup.onAllTightened.AddListener(MakeAvailable);
        else MakeAvailable();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void MakeAvailable()
    {
        if (available) return;
        available = true;
        onFlipAvailable.Invoke();
    }

    void Update()
    {
        // Bouton Y : lu de deux façons (bouton « logique » de la manette gauche, et bouton physique Y).
        bool pressed = OVRInput.GetDown(flipButton, controller) || OVRInput.GetDown(OVRInput.RawButton.Y);
        if (!pressed || flipping) return;

        // On explique toujours pourquoi le retournement est refusé.
        if (engine == null || blockRenderer == null)
            Debug.LogWarning("[Guide] retournement impossible : renseignez Engine (gamme1) et Block Renderer (block_moteur) sur Engine Flipper.");
        else if (placer != null && placer.CurrentState != WorkstationPlacer.State.Verrouille)
            Debug.Log("[Guide] retournement impossible : validez d'abord le placement du poste (A).");
        else if (!available)
            Debug.Log("[Guide] retournement pas encore disponible : terminez d'abord le serrage de la Gamme 1"
                      + " (ou videz Required Group).");
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

    // ---------- Pièces encore sur la table : elles ne tournent pas avec le bloc ----------

    private struct Loose { public Transform t; public Transform parent; public int sibling; }

    // Sort temporairement du moteur les pièces pas encore posées (elles restent où elles sont).
    private List<Loose> DetachLoosePieces()
    {
        var list = new List<Loose>();
        foreach (var p in FindObjectsByType<SnapToTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p.IsPlaced || !p.transform.IsChildOf(engine)) continue;
            list.Add(new Loose { t = p.transform, parent = p.transform.parent, sibling = p.transform.GetSiblingIndex() });
        }
        foreach (var l in list) l.t.SetParent(engine.parent, true);
        return list;
    }

    private static void Reattach(List<Loose> list)
    {
        foreach (var l in list)
        {
            if (l.t == null) continue;
            l.t.SetParent(l.parent, true);
            l.t.SetSiblingIndex(l.sibling);
        }
    }

    private IEnumerator Flip()
    {
        flipping = true;
        Debug.Log("[Guide] retournement du moteur...");

        var loose = DetachLoosePieces();

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
        Reattach(loose);
        if (placer != null) placer.RecomputeBottom();

        IsUpsideDown = !IsUpsideDown;
        flipping = false;
        Debug.Log("[Guide] moteur retourné (" + (IsUpsideDown ? "carter vers le haut" : "à l'endroit") + ").");
        onFlipped.Invoke();
    }
}
