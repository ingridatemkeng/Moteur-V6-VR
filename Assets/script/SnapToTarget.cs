using System.Collections;
using UnityEngine;
using UnityEngine.Events;

// À placer sur une pièce attrapable (ex. piston_head).
// Au relâchement, vérifie si la pièce est posée à la bonne POSITION et dans la bonne ORIENTATION.
// C'est la brique de base de l'emboîtement ET de la détection d'erreurs.
// V3 : le fantôme (cible) disparaît quand la pièce est bien placée, et RÉAPPARAÎT si on la retire.
//
// Câblage (PointableUnityEventWrapper de la pièce) :
//   When Select   -> SnapToTarget.OnGrabbed()
//   When Unselect -> SnapToTarget.CheckSnap()

public class SnapToTarget : MonoBehaviour
{
    [Header("Cible (pose assemblée correcte)")]
    public Transform target;                 // le fantôme placé à la position/orientation finale

    [Header("Tolérances")]
    public float positionTolerance = 0.03f;  // 3 cm
    public float angleTolerance = 15f;       // 15 degrés

    public enum Symmetry
    {
        Aucune,                 // la rotation complète compte (piston, bielle, chapeau, vilebrequin)
        RotationAutourAxe,      // la pièce peut tourner librement autour de son axe
        AxeEtRetournement       // idem, et elle peut aussi être montée retournée bout pour bout (axe de piston)
    }

    [Header("Symétrie (pièces cylindriques)")]
    [Tooltip("Aucune = rotation complète vérifiée. Pour piston_pin : Axe Et Retournement.")]
    public Symmetry symmetry = Symmetry.Aucune;
    [Tooltip("Axe de symétrie dans le repère LOCAL de la pièce : (1,0,0)=X rouge, (0,1,0)=Y vert, (0,0,1)=Z bleu.")]
    public Vector3 symmetryAxis = Vector3.up;

    [Header("Fantôme")]
    [Tooltip("Masque le fantôme quand la pièce est bien placée, le réaffiche si on la retire.")]
    public bool hideTargetWhenPlaced = true;

    [Header("Verrouillage")]
    [Tooltip("Coché (scénario montage) : une pièce bien posée ne peut plus être déplacée. Décoché : elle peut être retirée.")]
    public bool lockWhenPlaced = true;
    [Tooltip("Facultatif : l'objet de préhension à désactiver. Vide = trouvé automatiquement (enfant dont le nom contient 'GrabInteraction').")]
    public GameObject grabInteraction;

    [Header("Réactions (vibration, texte, guide 3D...)")]
    public UnityEvent onSnapped;             // montage correct
    public UnityEvent onWrongOrientation;    // bon endroit, mauvaise orientation
    public UnityEvent onWrongPlace;          // pas au bon endroit
    public UnityEvent onUnsnapped;           // pièce retirée après avoir été bien posée

    public bool IsPlaced => isPlaced;

    private Rigidbody rb;
    private bool isPlaced = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Recherche automatique de l'enfant de préhension créé par la Quick Action (ISDK_HandGrabInteraction).
        if (grabInteraction == null)
        {
            foreach (Transform child in transform)
            {
                if (child.name.Contains("GrabInteraction")) { grabInteraction = child.gameObject; break; }
            }
        }
    }

    // Pour un futur scénario (démontage, reprise) : rend la pièce de nouveau attrapable.
    public void Unlock()
    {
        if (grabInteraction != null) grabInteraction.SetActive(true);
    }

    // À appeler quand on attrape la pièce (When Select).
    // Si la pièce était posée, on la « désemboîte » : elle redevient à placer et le fantôme réapparaît.
    public void OnGrabbed()
    {
        if (!isPlaced) return;

        isPlaced = false;
        if (hideTargetWhenPlaced && target != null) target.gameObject.SetActive(true);
        Debug.Log(name + " : pièce retirée de sa position.");
        onUnsnapped.Invoke();
    }

    // À appeler au relâchement de la pièce (When Unselect).
    public void CheckSnap()
    {
        Debug.Log(name + " : CheckSnap APPELE");
        if (isPlaced || target == null) return;

        float distance = Vector3.Distance(transform.position, target.position);
        float angle = OrientationError();

        // Cas 1 : bon endroit ET bonne orientation -> montage correct
        if (distance <= positionTolerance && angle <= angleTolerance)
        {
            transform.position = target.position;   // on cale exactement en place
            transform.rotation = target.rotation;
            if (rb != null) rb.isKinematic = true;
            isPlaced = true;

            if (hideTargetWhenPlaced) target.gameObject.SetActive(false);

            Debug.Log(name + " : montage correct.");
            onSnapped.Invoke();

            // Verrouillage optionnel, à la frame suivante
            // (pas pendant l'événement de relâchement lui-même, pour éviter les conflits avec l'Interaction SDK).
            if (lockWhenPlaced)
            {
                if (grabInteraction != null) StartCoroutine(DisableGrabNextFrame());
                else Debug.LogWarning(name + " : verrouillage impossible, objet de préhension introuvable.");
            }
        }
        // Cas 2 : bon endroit MAIS mauvaise orientation -> erreur d'orientation
        else if (distance <= positionTolerance)
        {
            Debug.Log(name + " : mauvaise orientation (" + angle.ToString("F0") + " deg).");
            onWrongOrientation.Invoke();
        }
        // Cas 3 : pas au bon endroit
        else
        {
            Debug.Log(name + " : pas encore au bon endroit.");
            onWrongPlace.Invoke();
        }
    }

    // Écart d'orientation (en degrés) entre la pièce et sa cible, en tenant compte de la symétrie.
    // Pour une pièce symétrique, on ne compare que la direction de son axe :
    // la rotation autour de l'axe (et éventuellement le retournement) n'est pas une erreur.
    private float OrientationError()
    {
        if (symmetry == Symmetry.Aucune)
            return Quaternion.Angle(transform.rotation, target.rotation);

        Vector3 a = transform.TransformDirection(symmetryAxis).normalized;
        Vector3 b = target.TransformDirection(symmetryAxis).normalized;
        float angle = Vector3.Angle(a, b);

        if (symmetry == Symmetry.AxeEtRetournement)
            angle = Mathf.Min(angle, 180f - angle);

        return angle;
    }

    private IEnumerator DisableGrabNextFrame()
    {
        yield return null;
        grabInteraction.SetActive(false);
        Debug.Log(name + " : pièce verrouillée.");
    }
}