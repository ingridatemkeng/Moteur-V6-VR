using UnityEngine;

// Une douille montée sur une clé (cliquet, clé dynamométrique).
// À placer sur l'objet douille (ou sur un objet vide « Douille » si la douille fait partie du modèle de la clé).
// Le bout de la douille (côté écrou) devient le point de travail de la clé.
// Prévu pour plus tard : plusieurs douilles de tailles différentes dans la boîte à outils.

public class Socket : MonoBehaviour
{
    [Tooltip("Taille de la douille, écrite comme sur les écrous (ex. 1/2\", 13 mm).")]
    public string size = "1/2\"";

    [Tooltip("Objet vide placé au centre de l'ouverture de la douille (côté écrou).")]
    public Transform nutEnd;
}
