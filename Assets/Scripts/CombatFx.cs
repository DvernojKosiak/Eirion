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

    // =====================================================================
    // Звуки боя. Делаются прямо в коде (файлы не нужны): удар по мясу, свист клинка, звон блока...
    // =====================================================================
    public enum Snd { Flesh, FleshHeavy, Whoosh, Clang, Parry, Hurt, Thunk, Twang, Fall }

    static AudioClip[] clips;
    const int Rate = 44100;

    public static void Play(Snd kind, Vector3 at, float volume = 1f, float pitch = 1f)
    {
        if (clips == null) BuildClips();
        AudioClip c = clips[(int)kind];
        if (c == null) return;
        GameObject g = new GameObject("Sfx");
        g.transform.position = at;
        AudioSource a = g.AddComponent<AudioSource>();
        a.clip = c;
        a.volume = Mathf.Clamp01(volume);
        a.pitch = pitch * Random.Range(0.92f, 1.08f);
        a.spatialBlend = 0.7f;
        a.minDistance = 3f;
        a.maxDistance = 40f;
        a.rolloffMode = AudioRolloffMode.Linear;
        a.Play();
        Object.Destroy(g, c.length / Mathf.Max(0.3f, a.pitch) + 0.1f);
    }

    static void BuildClips()
    {
        clips = new AudioClip[9];
        clips[(int)Snd.Flesh] = Make("flesh", 0.22f, (t, n) => Thud(t, 150f, 55f, 22f) + Lp(n, 0.25f) * Mathf.Exp(-t * 35f) * 0.7f + n * Mathf.Exp(-t * 220f) * 0.5f);
        clips[(int)Snd.FleshHeavy] = Make("fleshHeavy", 0.38f, (t, n) => Thud(t, 120f, 38f, 11f) * 1.2f + Lp(n, 0.18f) * Mathf.Exp(-t * 18f) * 0.9f + n * Mathf.Exp(-t * 160f) * 0.6f);
        clips[(int)Snd.Whoosh] = Make("whoosh", 0.3f, (t, n) => { float k = Mathf.Sin(Mathf.PI * t / 0.3f); return Lp(n, 0.06f + 0.2f * k) * k * k * 1.6f; });
        clips[(int)Snd.Clang] = Make("clang", 0.65f, (t, n) => Bell(t, 1f) + n * Mathf.Exp(-t * 300f) * 0.7f);
        clips[(int)Snd.Parry] = Make("parry", 0.9f, (t, n) => Bell(t, 1.45f) * 1.2f + n * Mathf.Exp(-t * 250f) * 0.8f);
        clips[(int)Snd.Hurt] = Make("hurt", 0.28f, (t, n) => Thud(t, 95f, 42f, 14f) * 1.3f + Lp(n, 0.12f) * Mathf.Exp(-t * 30f) * 0.8f);
        clips[(int)Snd.Thunk] = Make("thunk", 0.14f, (t, n) => Thud(t, 260f, 120f, 38f) + n * Mathf.Exp(-t * 250f) * 0.5f);
        clips[(int)Snd.Twang] = Make("twang", 0.4f, (t, n) => (Mathf.Sin(2f * Mathf.PI * 165f * t + Mathf.Sin(t * 60f) * 0.6f) + 0.4f * Mathf.Sin(2f * Mathf.PI * 330f * t)) * Mathf.Exp(-t * 10f) + n * Mathf.Exp(-t * 120f) * 0.3f);
        clips[(int)Snd.Fall] = Make("fall", 0.32f, (t, n) => Thud(t, 80f, 35f, 13f) + Lp(n, 0.1f) * Mathf.Exp(-t * 20f) * 0.8f);
    }

    // Глухой удар: частота падает (как удар по телу), быстро затухает
    static float Thud(float t, float f0, float f1, float decay)
    {
        float f = Mathf.Lerp(f1, f0, Mathf.Exp(-t * 30f));
        return Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t * decay);
    }

    // Звон металла: несколько «негармоничных» тонов
    static float Bell(float t, float k)
    {
        return (Mathf.Sin(2f * Mathf.PI * 620f * k * t) * Mathf.Exp(-t * 6f)
              + 0.6f * Mathf.Sin(2f * Mathf.PI * 1047f * k * t) * Mathf.Exp(-t * 9f)
              + 0.4f * Mathf.Sin(2f * Mathf.PI * 1590f * k * t) * Mathf.Exp(-t * 12f)
              + 0.25f * Mathf.Sin(2f * Mathf.PI * 2380f * k * t) * Mathf.Exp(-t * 16f)) * 0.5f;
    }

    // Простой фильтр «приглушить» шум (a меньше — глуше)
    static float lpState;
    static float Lp(float x, float a) { lpState += a * (x - lpState); return lpState * 2f; }

    static AudioClip Make(string name, float dur, System.Func<float, float, float> gen)
    {
        int n = Mathf.CeilToInt(dur * Rate);
        float[] data = new float[n];
        lpState = 0f;
        System.Random rnd = new System.Random(name.GetHashCode());
        float peak = 0.0001f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
            float v = gen(t, noise);
            float fade = Mathf.Clamp01((dur - t) / 0.02f);   // без щелчка в конце
            data[i] = v * fade;
            peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        }
        for (int i = 0; i < n; i++) data[i] = data[i] / peak * 0.9f;
        AudioClip c = AudioClip.Create(name, n, 1, Rate, false);
        c.SetData(data, 0);
        return c;
    }
}
