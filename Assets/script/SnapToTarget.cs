using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

// À placer sur une pièce attrapable (ex. piston_head).
// Au relâchement, vérifie si la pièce est posée à la bonne POSITION et dans la bonne ORIENTATION.
// C'est la brique de base de l'emboîtement ET de la détection d'erreurs.

public class SnapToTarget : MonoBehaviour
{

    [Header("Cible (pose assemblée correcte)")]
    public Transform target;                 // repère placé à la position/orientation finale

    [Header("Tolérances")]
    public float positionTolerance = 0.03f;  // 3 cm
    public float angleTolerance = 15f;       // 15 degrés

    [Header("Réactions (à câbler plus tard : vibration, texte, guide 3D)")]
    public UnityEvent onSnapped;             // montage correct
    public UnityEvent onWrongOrientation;    // bon endroit, mauvaise orientation
    public UnityEvent onWrongPlace;          // pas au bon endroit

    private Rigidbody rb;
    private bool isPlaced = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    // À appeler au relâchement de la pièce
    // (via PointableUnityEventWrapper > When Unselect).
    public void CheckSnap()
    {
        Debug.Log(name + " : CheckSnap APPELE");
        if (isPlaced || target == null) return;

        float distance = Vector3.Distance(transform.position, target.position);
        float angle = Quaternion.Angle(transform.rotation, target.rotation);

        // Cas 1 : bon endroit ET bonne orientation -> montage correct
        if (distance <= positionTolerance && angle <= angleTolerance)
        {
            transform.position = target.position;   // on verrouille exactement en place
            transform.rotation = target.rotation;
            if (rb != null) rb.isKinematic = true;
            isPlaced = true;
            Debug.Log(name + " : montage correct.");
            onSnapped.Invoke();
        }
        // Cas 2 : bon endroit MAIS mauvaise orientation -> erreur d'orientation
        else if (distance <= positionTolerance && angle > angleTolerance)
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
}

