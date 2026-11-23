using UnityEngine;

// Плевок твари-плевальщика: летит дугой в точку, где был игрок. Можно увернуться — отбеги!
// Урон считает только хост (simulated), у остальных — просто картинка.
public class Projectile : MonoBehaviour
{
    Vector3 from, to;
    float t, duration;
    bool simulated;
    float damage;
    static Material mat;

    public static void Launch(Vector3 from, Vector3 to, bool simulated, float damage)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Spit";
        go.layer = 2;
        Destroy(go.GetComponent<Collider>());
        go.transform.position = from;
        go.transform.localScale = Vector3.one * 0.4f;
        go.GetComponent<Renderer>().sharedMaterial = Mat();
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point; l.color = new Color(0.5f, 1f, 0.3f); l.range = 4f; l.intensity = 2f; l.shadows = LightShadows.None;
        Projectile p = go.AddComponent<Projectile>();
        p.from = from; p.to = to; p.simulated = simulated; p.damage = damage;
        p.duration = Mathf.Max(0.4f, Vector3.Distance(from, to) / 15f);
    }

    static Material Mat()
    {
        if (mat != null) return mat;
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        mat = new Material(s);
        Color c = new Color(0.45f, 0.9f, 0.2f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_EmissionColor")) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", c * 3f); }
        return mat;
    }

    void Update()
    {
        t += Time.deltaTime / duration;
        Vector3 p = Vector3.Lerp(from, to, t) + Vector3.up * Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI) * 2.5f;
        transform.position = p;
        if (t < 1f) return;
        if (simulated && EnemyManager.Instance != null) EnemyManager.Instance.SplashAt(to, 1.8f, damage);
        // Брызги
        for (int i = 0; i < 5; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            d.layer = 2;
            Destroy(d.GetComponent<Collider>());
            d.transform.position = to + Random.insideUnitSphere * 0.5f;
            d.transform.localScale = Vector3.one * Random.Range(0.1f, 0.25f);
            d.GetComponent<Renderer>().sharedMaterial = Mat();
            Destroy(d, 1.2f);
        }
        Destroy(gameObject);
    }
}
