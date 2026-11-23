using UnityEngine;

// «Сочность» ударов: короткое замедление времени в момент попадания (hitstop) и искры.
public static class CombatFx
{
    static float stopUntil;
    static bool stopping;

    // Замедлить время на duration секунд (реальных)
    public static void Hitstop(float duration)
    {
        stopUntil = Mathf.Max(stopUntil, Time.unscaledTime + duration);
        stopping = true;
        Time.timeScale = 0.05f;
    }

    // Вызывать каждый кадр (это делает PlayerCombat)
    public static void Tick()
    {
        if (stopping && Time.unscaledTime >= stopUntil)
        {
            stopping = false;
            Time.timeScale = 1f;
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
}
