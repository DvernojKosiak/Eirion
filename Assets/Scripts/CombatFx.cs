using UnityEngine;

// «Сочность» ударов: короткое замедление времени в момент попадания (hitstop) и искры.
public static class CombatFx
{
    static float stopUntil, readyAt;
    static bool stopping;

    // Чуть притормозить время на duration секунд (реальных) — удар «вязнет», чувствуется вес.
    // Не до полной остановки и не чаще раза в 0.15 сек, чтобы не дёргалось.
    public static void Hitstop(float duration)
    {
        if (!stopping && Time.unscaledTime < readyAt) return;
        stopUntil = Mathf.Max(stopUntil, Time.unscaledTime + Mathf.Min(duration, 0.09f));
        stopping = true;
        Time.timeScale = 0.15f;
    }

    // Вызывать каждый кадр (это делает PlayerCombat). Время возвращается плавно.
    public static void Tick()
    {
        if (!stopping) return;
        if (Time.unscaledTime >= stopUntil)
        {
            Time.timeScale = Mathf.MoveTowards(Time.timeScale, 1f, Time.unscaledDeltaTime * 12f);
            if (Time.timeScale >= 1f) { stopping = false; readyAt = Time.unscaledTime + 0.15f; }
        }
    }

    static Material spark;

    // Искры в месте удара
    public static void Sparks(Vector3 at, Color color, int count)
    {
        if (spark == null)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            spark = new Material(s != null ? s : Shader.Find("Standard"));
            if (spark.HasProperty("_BaseColor")) spark.SetColor("_BaseColor", Color.white);
            if (spark.HasProperty("_EmissionColor")) { spark.EnableKeyword("_EMISSION"); spark.SetColor("_EmissionColor", new Color(1f, 0.6f, 0.3f) * 3f); }
        }
        for (int i = 0; i < count; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.layer = 2;
            Object.Destroy(d.GetComponent<Collider>());
            d.transform.position = at;
            d.transform.localScale = new Vector3(0.04f, 0.04f, Random.Range(0.1f, 0.25f));
            Vector3 v = Random.insideUnitSphere * 5f + Vector3.up * 2f;
            d.transform.rotation = Quaternion.LookRotation(v);
            Renderer r = d.GetComponent<Renderer>();
            r.sharedMaterial = spark;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            d.AddComponent<FlyAway>().velocity = v;
            Object.Destroy(d, 0.35f);
        }
    }

    static Material ichor, poison;

    static Material Mat(ref Material m, Color c, float glow)
    {
        if (m != null) return m;
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        m = new Material(s != null ? s : Shader.Find("Standard"));
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (glow > 0f && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * glow); }
        return m;
    }

    // Тёмная жижа тварей: брызжет туда, куда шёл клинок
    public static void Ichor(Vector3 at, Vector3 dir, int count)
    {
        Material m = Mat(ref ichor, new Color(0.18f, 0.04f, 0.22f), 0.6f);
        Vector3 d = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.up;
        for (int i = 0; i < count; i++)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            g.layer = 2;
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.position = at + Random.insideUnitSphere * 0.1f;
            g.transform.localScale = Vector3.one * Random.Range(0.05f, 0.13f);
            Renderer r = g.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            g.AddComponent<FlyAway>().velocity = d * Random.Range(2f, 6f) + Random.insideUnitSphere * 2.2f + Vector3.up * 1.5f;
            Object.Destroy(g, Random.Range(0.4f, 0.8f));
        }
    }

    // Ядовитый взрыв мешка плевальщика
    public static void Poison(Vector3 at, int count)
    {
        Material m = Mat(ref poison, new Color(0.45f, 0.9f, 0.2f), 3f);
        for (int i = 0; i < count; i++)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            g.layer = 2;
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.position = at + Random.insideUnitSphere * 0.3f;
            g.transform.localScale = Vector3.one * Random.Range(0.12f, 0.35f);
            g.GetComponent<Renderer>().sharedMaterial = m;
            g.AddComponent<FlyAway>().velocity = Random.insideUnitSphere * 7f + Vector3.up * 3f;
            Object.Destroy(g, Random.Range(0.6f, 1.1f));
        }
    }
}
