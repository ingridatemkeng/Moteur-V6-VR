using UnityEngine;

// N'affiche qu'UN seul fantôme à la fois : celui de la première pièce de la liste qui n'est pas encore posée.
// - Supprime le clignotement dû aux fantômes transparents superposés.
// - Guide l'apprenant étape par étape (embryon du futur graphe de précédence).
// À placer sur un objet de la scène (ex. gamme1). Remplir la liste dans l'ordre de la gamme.

public class GhostSequence : MonoBehaviour
{
    [Tooltip("Les pièces dans l'ordre de montage (ex. crank_shaft, piston_head, piston_pin, piston_rod, piston_cap_rod).")]
    public SnapToTarget[] steps;

    void Start()
    {
        foreach (var s in steps)
        {
            if (s == null) continue;
            s.onSnapped.AddListener(Refresh);
            s.onUnsnapped.AddListener(Refresh);
        }
        Refresh();
    }

    // Masque tous les fantômes, puis affiche seulement celui de l'étape en cours.
    public void Refresh()
    {
        SnapToTarget current = null;

        foreach (var s in steps)
        {
            if (s == null || s.target == null) continue;
            s.target.gameObject.SetActive(false);
            if (current == null && !s.IsPlaced) current = s;
        }

        if (current != null)
        {
            current.target.gameObject.SetActive(true);
            Debug.Log("[Guide] étape en cours : " + current.name);
        }
        else
        {
            Debug.Log("[Guide] toutes les pièces sont posées.");
        }
    }
}