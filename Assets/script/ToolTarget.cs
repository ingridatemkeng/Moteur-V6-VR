using UnityEngine;

// Les types d'outils de la boîte à outils. Ajouter un outil = ajouter une ligne ici.
public enum ToolType
{
    CleDynamometrique,
    Tournevis,
    CleMolette,
    CleAllen,
    Burette
}

public static class ToolNames
{
    public static string Of(ToolType t)
    {
        switch (t)
        {
            case ToolType.CleDynamometrique: return "clé dynamométrique";
            case ToolType.Tournevis: return "tournevis";
            case ToolType.CleMolette: return "clé à molette";
            case ToolType.CleAllen: return "clé Allen";
            case ToolType.Burette: return "burette d'huile";
            default: return t.ToString();
        }
    }

    // Avec l'article, pour les messages : « il faut une clé dynamométrique ».
    public static string WithArticle(ToolType t)
    {
        switch (t)
        {
            case ToolType.Tournevis: return "un tournevis";
            default: return "une " + Of(t);
        }
    }
}

// Base de tout ce sur quoi un outil peut agir (écrou, pièce à huiler...).
// Chaque cible déclare l'outil qu'elle attend : un autre outil déclenche « mauvais outil ».
public abstract class ToolTarget : MonoBehaviour
{
    [Header("Outil attendu")]
    public ToolType requiredTool = ToolType.CleDynamometrique;

    // La cible accepte-t-elle un outil en ce moment ? (ex. écrou actif, pas encore serré)
    public abstract bool AcceptsTool { get; }

    // Point où l'outil doit venir : le centre visible de l'objet (fiable même si son pivot est décalé).
    public virtual Vector3 WorkPoint
    {
        get
        {
            Renderer r = GetComponent<Renderer>();
            return r != null ? r.bounds.center : transform.position;
        }
    }

    // Axe autour duquel on mesure la rotation de l'outil (axe de la vis pour un écrou).
    public virtual Vector3 ToolAxis => transform.up;

    public virtual void OnToolEngaged(Tool tool) { }
    public virtual void OnToolRotate(float degrees) { }
    public virtual void OnToolDisengaged() { }
}