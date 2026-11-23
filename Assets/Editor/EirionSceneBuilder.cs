using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Скрипт ДЛЯ РЕДАКТОРА (не для игры). Лежит в папке Assets/Editor.
// Добавляет в верхнее меню Unity пункт "Eirion -> Создать сцену форта".
// Он сам строит черновую карту из простых фигур и сохраняет её:
//   форт с частоколом, башнями, воротами и стеной для обороны (на неё можно забежать по пандусу),
//   деревню за фортом (её мы защищаем), площадку для дирижабля,
//   лес на севере (зона вылазок) и стену тьмы на горизонте.
// Направления: север (+Z) — к тьме, юг (-Z) — к деревне.
// Потом модельеры заменят эти кубы на настоящие модели.
public static class EirionSceneBuilder
{
    const string ScenePath = "Assets/Scenes/Fort.unity";

    // Размеры форта
    const float FortHalf = 15f;   // форт 30 x 30 метров
    const float WallHeight = 4f;
    const float WallThick = 1f;
    const float GateHalf = 3f;    // ворота шириной 6 метров
    const float RampartHeight = 3f; // высота помоста, на котором стоят защитники
    const float RampartWidth = 2f;

    static Transform root;

    [MenuItem("Eirion/Создать сцену форта")]
    public static void CreateFortScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
            !EditorUtility.DisplayDialog("Eirion", "Сцена форта уже есть. Пересоздать её заново?\n(Всё, что ты в ней поменял руками, пропадёт.)", "Да", "Отмена"))
            return;

        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        Material grass  = Mat("Grass",  new Color(0.27f, 0.33f, 0.22f));
        Material wood   = Mat("Wood",   new Color(0.40f, 0.28f, 0.17f));
        Material stone  = Mat("Stone",  new Color(0.45f, 0.45f, 0.47f));
        Material roof   = Mat("Roof",   new Color(0.35f, 0.15f, 0.12f));
        Material leaves = Mat("Leaves", new Color(0.16f, 0.26f, 0.14f));
        Material player = Mat("Player", new Color(0.70f, 0.25f, 0.20f));
        Material crystal = Mat("Lailillium", new Color(0.55f, 0.35f, 1f), new Color(0.6f, 0.35f, 1f) * 3f);
        Material dark   = Mat("Darkness", new Color(0.05f, 0.02f, 0.08f), new Color(0.25f, 0.05f, 0.35f));

        SetupAtmosphere();

        // --- Земля 300 x 300 метров ---
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(30f, 1f, 30f);
        ground.GetComponent<Renderer>().sharedMaterial = grass;
        ground.isStatic = true;

        BuildFort(wood, stone, crystal);
        BuildVillage(wood, roof);
        BuildForest(wood, leaves);
        BuildDarkness(dark);
        BuildPlayer(player);

        // --- Сохраняем сцену и ставим её первой в списке сцен игры ---
        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path != ScenePath) scenes.Add(s);
        EditorBuildSettings.scenes = scenes.ToArray();

        Selection.activeGameObject = GameObject.Find("Player");
        EditorUtility.DisplayDialog("Eirion", "Готово! Сцена Fort создана и сохранена.\nНажми ▶ Play сверху.", "Ок");
    }

    // ---------------- Форт ----------------
    static void BuildFort(Material wood, Material stone, Material crystal)
    {
        root = new GameObject("Fort").transform;
        float h = FortHalf;
        float wy = WallHeight / 2f;

        // Северная стена (к тьме) и южная (к деревне) — с воротами посередине
        float segLen = h - GateHalf;              // длина куска стены от угла до ворот
        float segCenter = GateHalf + segLen / 2f; // центр этого куска
        foreach (float z in new[] { h, -h })
        {
            string side = z > 0 ? "North" : "South";
            Box("Wall_" + side + "_L", new Vector3(-segCenter, wy, z), new Vector3(segLen, WallHeight, WallThick), wood);
            Box("Wall_" + side + "_R", new Vector3(segCenter, wy, z), new Vector3(segLen, WallHeight, WallThick), wood);

            float rz = z - Mathf.Sign(z) * (WallThick / 2f + RampartWidth / 2f);
            Box("Rampart_" + side + "_L", new Vector3(-segCenter, RampartHeight / 2f, rz), new Vector3(segLen, RampartHeight, RampartWidth), wood);
            Box("Rampart_" + side + "_R", new Vector3(segCenter, RampartHeight / 2f, rz), new Vector3(segLen, RampartHeight, RampartWidth), wood);

            // Столбы ворот
            Box("GatePost_" + side + "_L", new Vector3(-GateHalf - 0.5f, 3f, z), new Vector3(1f, 6f, 1.4f), wood);
            Box("GatePost_" + side + "_R", new Vector3(GateHalf + 0.5f, 3f, z), new Vector3(1f, 6f, 1.4f), wood);
        }

        // Западная и восточная стены — сплошные
        foreach (float x in new[] { -h, h })
        {
            string side = x < 0 ? "West" : "East";
            Box("Wall_" + side, new Vector3(x, wy, 0f), new Vector3(WallThick, WallHeight, h * 2f), wood);
            float rx = x - Mathf.Sign(x) * (WallThick / 2f + RampartWidth / 2f);
            Box("Rampart_" + side, new Vector3(rx, RampartHeight / 2f, 0f), new Vector3(RampartWidth, RampartHeight, h * 2f - 2f * (WallThick + RampartWidth)), wood);
        }

        // Башни по углам
        foreach (float x in new[] { -h, h })
            foreach (float z in new[] { -h, h })
                Box("Tower", new Vector3(x, 4f, z), new Vector3(3.5f, 8f, 3.5f), stone);

        // Пандус: с земли во дворе на западный помост
        float rampStartX = -2f;
        float rampEndX = -h + WallThick / 2f + RampartWidth; // край западного помоста
        float run = rampStartX - rampEndX;
        float len = Mathf.Sqrt(run * run + RampartHeight * RampartHeight);
        float angle = Mathf.Atan2(RampartHeight, run) * Mathf.Rad2Deg;
        GameObject ramp = Box("Ramp", new Vector3((rampStartX + rampEndX) / 2f, RampartHeight / 2f - 0.1f, 0f), new Vector3(len, 0.3f, 3f), wood);
        ramp.transform.rotation = Quaternion.Euler(0f, 0f, -angle);

        // Кристалл лайлилиума в центре двора (просто для красоты, пока)
        GameObject c = Box("Lailillium_Crystal", new Vector3(5f, 1.2f, -6f), new Vector3(0.8f, 2.4f, 0.8f), crystal);
        c.transform.rotation = Quaternion.Euler(0f, 45f, 10f);
        GameObject lightGo = new GameObject("Crystal_Light");
        lightGo.transform.SetParent(c.transform, false);
        Light l = lightGo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(0.65f, 0.4f, 1f);
        l.range = 8f;
        l.intensity = 3f;

        // Костёр-заглушка
        Box("Campfire", new Vector3(-4f, 0.25f, -8f), new Vector3(1.2f, 0.5f, 1.2f), stone);
    }

    // ---------------- Деревня (за фортом, на юге) ----------------
    static void BuildVillage(Material wood, Material roof)
    {
        root = new GameObject("Village").transform;
        Vector3[] spots =
        {
            new Vector3(-14f, 0f, -38f), new Vector3(-4f, 0f, -42f), new Vector3(7f, 0f, -38f),
            new Vector3(17f, 0f, -44f), new Vector3(-10f, 0f, -55f), new Vector3(4f, 0f, -57f),
            new Vector3(15f, 0f, -60f),
        };
        for (int i = 0; i < spots.Length; i++)
        {
            Vector3 p = spots[i];
            float yaw = (i * 37f) % 40f - 20f;
            GameObject house = new GameObject("House_" + (i + 1));
            house.transform.SetParent(root, false);
            house.transform.position = p;
            house.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            Transform keep = root;
            root = house.transform;
            Box("Walls", p + Vector3.up * 1.5f, new Vector3(5f, 3f, 6f), wood).transform.rotation = house.transform.rotation;
            // Крыша: вытянутый куб, повёрнутый на 45 градусов — получается «домик»
            GameObject r = Box("Roof", p + Vector3.up * 3.2f, new Vector3(3.9f, 3.9f, 6.4f), roof);
            r.transform.rotation = house.transform.rotation * Quaternion.Euler(0f, 0f, 45f);
            root = keep;
        }

        // Площадка для дирижабля
        GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = "Airship_LandingPad";
        pad.transform.SetParent(root, false);
        pad.transform.position = new Vector3(30f, 0.1f, -25f);
        pad.transform.localScale = new Vector3(14f, 0.1f, 14f);
        pad.GetComponent<Renderer>().sharedMaterial = wood;
        pad.isStatic = true;
    }

    // ---------------- Лес (зона вылазок, на севере) ----------------
    static void BuildForest(Material wood, Material leaves)
    {
        root = new GameObject("Forest").transform;
        Random.State saved = Random.state;
        Random.InitState(1234); // одинаковый лес при каждом пересоздании

        int placed = 0;
        int tries = 0;
        while (placed < 90 && tries < 1000)
        {
            tries++;
            Vector3 p = new Vector3(Random.Range(-130f, 130f), 0f, Random.Range(28f, 135f));
            if (Mathf.Abs(p.x) < 6f && p.z < 60f) continue; // оставляем тропу от ворот
            float s = Random.Range(0.8f, 1.4f);

            GameObject tree = new GameObject("Tree");
            tree.transform.SetParent(root, false);
            tree.transform.position = p;
            tree.isStatic = true;

            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0f, 2f * s, 0f);
            trunk.transform.localScale = new Vector3(0.5f * s, 2f * s, 0.5f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = wood;
            trunk.isStatic = true;

            GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            crown.name = "Crown";
            crown.transform.SetParent(tree.transform, false);
            crown.transform.localPosition = new Vector3(0f, 5f * s, 0f);
            crown.transform.localScale = Vector3.one * 3.5f * s;
            crown.GetComponent<Renderer>().sharedMaterial = leaves;
            Object.DestroyImmediate(crown.GetComponent<Collider>()); // сквозь крону можно проходить
            crown.isStatic = true;

            placed++;
        }
        Random.state = saved;
    }

    // ---------------- Граница тьмы ----------------
    static void BuildDarkness(Material dark)
    {
        root = new GameObject("Darkness").transform;
        GameObject wall = Box("Darkness_Wall", new Vector3(0f, 30f, 148f), new Vector3(300f, 60f, 2f), dark);
        Object.DestroyImmediate(wall.GetComponent<Collider>());
        // Невидимая стена, чтобы не уйти за край карты
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0f, 10f, 150f),  new Vector3(300f, 20f, 1f)),
            (new Vector3(0f, 10f, -150f), new Vector3(300f, 20f, 1f)),
            (new Vector3(150f, 10f, 0f),  new Vector3(1f, 20f, 300f)),
            (new Vector3(-150f, 10f, 0f), new Vector3(1f, 20f, 300f)),
        })
        {
            GameObject b = Box("MapBorder", pos, size, dark);
            Object.DestroyImmediate(b.GetComponent<Renderer>());
            Object.DestroyImmediate(b.GetComponent<MeshFilter>());
        }
    }

    // ---------------- Герой и камера ----------------
    static void BuildPlayer(Material mat)
    {
        GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.transform.position = new Vector3(0f, 1f, -8f);
        player.GetComponent<Renderer>().sharedMaterial = mat;
        Object.DestroyImmediate(player.GetComponent<CapsuleCollider>());
        player.AddComponent<CharacterController>();
        player.AddComponent<PlayerMovement>();

        GameObject pivot = new GameObject("CameraPivot");
        pivot.transform.SetParent(player.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 0.6f, 0f);

        Camera cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
        }
        cam.transform.SetParent(pivot.transform, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 400f;
        EnableAntiAliasing(cam);

        pivot.AddComponent<PlayerCamera>();
        cam.gameObject.AddComponent<FpsCounter>();
    }

    // ---------------- Атмосфера: туман, свет ----------------
    static void SetupAtmosphere()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.42f, 0.44f, 0.48f);
        RenderSettings.fogDensity = 0.008f;

        Light sun = Object.FindFirstObjectByType<Light>();
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            sun.color = new Color(1f, 0.92f, 0.82f);
            sun.intensity = 1.1f;
        }
    }

    // Сглаживание «лесенки» на краях объектов (SMAA)
    static void EnableAntiAliasing(Camera cam)
    {
        System.Type t = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        if (t == null) return;
        Component data = cam.GetComponent(t);
        if (data == null) data = cam.gameObject.AddComponent(t);
        var so = new SerializedObject(data);
        SerializedProperty aa = so.FindProperty("m_Antialiasing");
        SerializedProperty q = so.FindProperty("m_AntialiasingQuality");
        if (aa != null) aa.intValue = 2; // 2 = SMAA
        if (q != null) q.intValue = 2;   // высокое качество
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---------------- Помощники ----------------
    static GameObject Box(string name, Vector3 pos, Vector3 size, Material mat)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(root, false);
        box.transform.position = pos;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = mat;
        box.isStatic = true;
        return box;
    }

    // Материал (цвет поверхности) — сохраняется файлом в Assets/Materials
    static Material Mat(string name, Color color, Color? emission = null)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = "Assets/Materials/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (emission.HasValue && mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission.Value);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
