using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Скрипт ДЛЯ РЕДАКТОРА (не для игры). Лежит в папке Assets/Editor.
// Меню "Eirion -> Создать сцену форта":
//   1) делает детали построек (префабы) в Assets/Prefabs/Building;
//   2) собирает из них большой форт — всё можно ломать и перестраивать;
//   3) ставит деревню, лес, границу тьмы, героя с мечом и режимом стройки.
// Направления: север (+Z) — к тьме, юг (-Z) — к деревне.
public static class EirionSceneBuilder
{
    const string ScenePath = "Assets/Scenes/Fort.unity";
    const string PrefabFolder = "Assets/Prefabs/Building";

    const float FortHalf = 26f;     // форт 52 x 52 метра
    const float FloorY = 2.7f;      // высота помостов вдоль стен

    static Transform root;

    // Материалы
    static Material grass, wood, darkWood, stone, roof, leaves, playerMat, crystal, dark, steel, ghost;

    // Детали конструктора
    static BuildPiece pPalisade, pPalisadeGate, pPlank, pPlankHalf, pPlankWindow, pPlankDoor,
                      pFloor, pStairs, pPillar, pRoof, pGable, pBarricade,
                      pStone, pStoneHalf, pStoneSlit, pStoneArch, pTower;
    static List<BuildPiece> catalog;

    [MenuItem("Eirion/Создать сцену форта")]
    public static void CreateFortScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
            !EditorUtility.DisplayDialog("Eirion", "Сцена форта уже есть. Пересоздать её заново?\n(Всё, что ты в ней поменял руками, пропадёт.)", "Да", "Отмена"))
            return;

        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        MakeMaterials();
        MakePiecePrefabs();
        SetupAtmosphere();

        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Plane);
        g.name = "Ground";
        g.transform.localScale = new Vector3(40f, 1f, 40f); // 400 x 400 метров
        g.GetComponent<Renderer>().sharedMaterial = grass;
        g.isStatic = true;

        GameObject manager = new GameObject("BuildingManager");
        manager.AddComponent<BuildingManager>();

        BuildFort();
        BuildVillage();
        BuildForest();
        BuildDarkness();
        BuildPlayer();

        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path != ScenePath && !s.path.EndsWith("TestScene.unity")) scenes.Add(s);
        EditorBuildSettings.scenes = scenes.ToArray();

        Selection.activeGameObject = GameObject.Find("Player");
        EditorUtility.DisplayDialog("Eirion", "Готово! Сцена Fort создана и сохранена.\nНажми ▶ Play сверху.", "Ок");
    }

    // ================= Материалы =================
    static void MakeMaterials()
    {
        grass    = Mat("Grass",    new Color(0.27f, 0.33f, 0.22f));
        wood     = Mat("Wood",     new Color(0.42f, 0.30f, 0.18f));
        darkWood = Mat("DarkWood", new Color(0.28f, 0.19f, 0.11f));
        stone    = Mat("Stone",    new Color(0.45f, 0.45f, 0.47f));
        roof     = Mat("Roof",     new Color(0.35f, 0.15f, 0.12f));
        leaves   = Mat("Leaves",   new Color(0.16f, 0.26f, 0.14f));
        playerMat= Mat("Player",   new Color(0.70f, 0.25f, 0.20f));
        steel    = Mat("Steel",    new Color(0.75f, 0.77f, 0.8f));
        crystal  = Mat("Lailillium", new Color(0.55f, 0.35f, 1f), new Color(0.6f, 0.35f, 1f) * 3f);
        dark     = Mat("Darkness", new Color(0.05f, 0.02f, 0.08f), new Color(0.25f, 0.05f, 0.35f));
        ghost    = GhostMat();
    }

    // ================= Детали построек =================
    static void MakePiecePrefabs()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Prefabs", "Building");
        catalog = new List<BuildPiece>();
        GameObject go;

        // ---------- ДЕРЕВО ----------
        // Частокол 4 x 4 м из брёвен
        go = new GameObject("WoodPalisade");
        for (int i = 0; i < 8; i++)
        {
            float h = 4f + ((i * 7) % 3) * 0.15f;
            Part(go.transform, PrimitiveType.Cylinder, new Vector3(-1.75f + i * 0.5f, h / 2f, 0f), new Vector3(0.5f, h / 2f, 0.5f), wood, Vector3.zero, false);
        }
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2.8f, -0.3f), new Vector3(4f, 0.25f, 0.15f), darkWood, Vector3.zero, false);
        RootBox(go, new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 0.5f));
        pPalisade = SavePiece(go, "Частокол", "Дерево", 300f, new Vector3(4f, 4f, 0.5f), 4, 0);

        // Ворота частокола (проход 2.4 x 3.3 м)
        go = new GameObject("WoodPalisadeGate");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.6f, 2.25f, 0f), new Vector3(0.8f, 4.5f, 0.8f), darkWood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.6f, 2.25f, 0f), new Vector3(0.8f, 4.5f, 0.8f), darkWood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.9f, 0f), new Vector3(2.4f, 1.2f, 0.6f), wood);
        pPalisadeGate = SavePiece(go, "Ворота частокола", "Дерево", 450f, new Vector3(4f, 4.5f, 0.8f), 6, 0);

        // Стена из досок 4 x 3 м
        go = new GameObject("PlankWall");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 0.3f), wood);
        Frame(go, 3f);
        pPlank = SavePiece(go, "Стена из досок", "Дерево", 250f, new Vector3(4f, 3f, 0.3f), 3, 0);

        // Полустена 4 x 1.5 м
        go = new GameObject("PlankWallHalf");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.75f, 0f), new Vector3(4f, 1.5f, 0.3f), wood);
        Frame(go, 1.5f);
        pPlankHalf = SavePiece(go, "Полустена", "Дерево", 150f, new Vector3(4f, 1.5f, 0.3f), 2, 0);

        // Стена с окном (окно 1.2 x 1 м)
        go = new GameObject("PlankWallWindow");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2.6f, 0f), new Vector3(4f, 0.8f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.3f, 1.7f, 0f), new Vector3(1.4f, 1f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.3f, 1.7f, 0f), new Vector3(1.4f, 1f, 0.3f), wood);
        Frame(go, 3f);
        pPlankWindow = SavePiece(go, "Стена с окном", "Дерево", 220f, new Vector3(4f, 3f, 0.3f), 3, 0);

        // Стена с дверным проёмом (1.4 x 2.2 м)
        go = new GameObject("PlankWallDoor");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.35f, 1.5f, 0f), new Vector3(1.3f, 3f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.35f, 1.5f, 0f), new Vector3(1.3f, 3f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2.6f, 0f), new Vector3(1.4f, 0.8f, 0.3f), wood);
        Frame(go, 3f);
        pPlankDoor = SavePiece(go, "Стена с дверью", "Дерево", 220f, new Vector3(4f, 3f, 0.3f), 3, 0);

        // Пол / помост 4 x 4 м
        go = new GameObject("WoodFloor");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.15f, 0f), new Vector3(4f, 0.3f, 4f), wood);
        pFloor = SavePiece(go, "Пол / помост", "Дерево", 200f, new Vector3(4f, 0.3f, 4f), 3, 0);

        // Лестница: +3 м на длине 4 м
        go = new GameObject("WoodStairs");
        float sa = Mathf.Atan2(3f, 4f) * Mathf.Rad2Deg;
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(2f, 0.3f, 5f), wood, new Vector3(-sa, 0f, 0f));
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 1.9f), new Vector3(1.8f, 3f, 0.2f), darkWood, Vector3.zero, false);
        pStairs = SavePiece(go, "Лестница", "Дерево", 200f, new Vector3(2f, 3f, 4f), 3, 0);

        // Столб 3 м
        go = new GameObject("WoodPillar");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(0.5f, 3f, 0.5f), darkWood);
        pPillar = SavePiece(go, "Столб", "Дерево", 150f, new Vector3(0.5f, 3f, 0.5f), 1, 0);

        // Скат крыши: 4 м в ширину, поднимается на 2 м на длине 2 м (45°)
        go = new GameObject("WoodRoof");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(4.2f, 0.15f, 2.95f), roof, new Vector3(-45f, 0f, 0f));
        pRoof = SavePiece(go, "Скат крыши", "Дерево", 150f, new Vector3(4f, 2f, 2f), 2, 0);

        // Фронтон — треугольник под крышей, 4 x 2 м
        go = new GameObject("WoodGable");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.335f, 0f), new Vector3(4f, 0.67f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.0f, 0f), new Vector3(2.67f, 0.67f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.665f, 0f), new Vector3(1.33f, 0.67f, 0.3f), wood);
        pGable = SavePiece(go, "Фронтон", "Дерево", 120f, new Vector3(4f, 2f, 0.3f), 2, 0);

        // Баррикада с кольями
        go = new GameObject("Barricade");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), new Vector3(4f, 0.5f, 0.5f), darkWood);
        for (int i = 0; i < 5; i++)
            Part(go.transform, PrimitiveType.Cube, new Vector3(-1.6f + i * 0.8f, 0.8f, 0.3f), new Vector3(0.15f, 0.15f, 2f), wood, new Vector3(-40f, 0f, 0f));
        pBarricade = SavePiece(go, "Баррикада", "Дерево", 150f, new Vector3(4f, 1.5f, 1.5f), 3, 0);

        // ---------- КАМЕНЬ ----------
        // Каменная стена 4 x 4 м с зубцами
        go = new GameObject("StoneWall");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 1f), stone);
        Crenels(go, 4f);
        pStone = SavePiece(go, "Каменная стена", "Камень", 900f, new Vector3(4f, 4.6f, 1f), 0, 6);

        // Каменная полустена 4 x 2 м
        go = new GameObject("StoneWallHalf");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(4f, 2f, 1f), stone);
        pStoneHalf = SavePiece(go, "Каменная полустена", "Камень", 500f, new Vector3(4f, 2f, 1f), 0, 3);

        // Стена с бойницей (узкая щель для лука)
        go = new GameObject("StoneWallSlit");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.1f, 2f, 0f), new Vector3(1.8f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.1f, 2f, 0f), new Vector3(1.8f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(0.4f, 1.2f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.4f, 0f), new Vector3(0.4f, 1.2f, 1f), stone);
        Crenels(go, 4f);
        pStoneSlit = SavePiece(go, "Стена с бойницей", "Камень", 850f, new Vector3(4f, 4.6f, 1f), 0, 6);

        // Каменная арка (проход 2 x 3 м)
        go = new GameObject("StoneArch");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.5f, 2f, 0f), new Vector3(1f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.5f, 2f, 0f), new Vector3(1f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.5f, 0f), new Vector3(2f, 1f, 1f), stone);
        Crenels(go, 4f);
        pStoneArch = SavePiece(go, "Каменная арка", "Камень", 800f, new Vector3(4f, 4.6f, 1f), 0, 6);

        // Башня 4 x 4 x 8 м
        go = new GameObject("StoneTower");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 4f, 0f), new Vector3(4f, 8f, 4f), stone);
        foreach (float x in new[] { -1.5f, 1.5f })
            foreach (float z in new[] { -1.5f, 1.5f })
                Part(go.transform, PrimitiveType.Cube, new Vector3(x, 8.35f, z), new Vector3(1f, 0.7f, 1f), stone);
        pTower = SavePiece(go, "Башня", "Камень", 1500f, new Vector3(4f, 8.7f, 4f), 4, 20);
    }

    // Тёмные столбики по краям деревянной стены — «рамка»
    static void Frame(GameObject go, float height)
    {
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.9f, height / 2f, 0f), new Vector3(0.25f, height, 0.4f), darkWood, Vector3.zero, false);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.9f, height / 2f, 0f), new Vector3(0.25f, height, 0.4f), darkWood, Vector3.zero, false);
    }

    // Зубцы сверху каменной стены
    static void Crenels(GameObject go, float top)
    {
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.25f, top + 0.3f, 0f), new Vector3(1f, 0.6f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.25f, top + 0.3f, 0f), new Vector3(1f, 0.6f, 1f), stone);
    }

    static void RootBox(GameObject go, Vector3 center, Vector3 size)
    {
        BoxCollider bc = go.AddComponent<BoxCollider>();
        bc.center = center;
        bc.size = size;
    }

    static GameObject Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default, bool collider = true)
    {
        GameObject p = GameObject.CreatePrimitive(type);
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        p.transform.localRotation = Quaternion.Euler(euler);
        p.GetComponent<Renderer>().sharedMaterial = mat;
        if (!collider) Object.DestroyImmediate(p.GetComponent<Collider>());
        return p;
    }

    static BuildPiece SavePiece(GameObject go, string displayName, string category, float hp, Vector3 size, int wood, int stone)
    {
        BuildPiece bp = go.AddComponent<BuildPiece>();
        bp.pieceId = go.name;
        bp.displayName = displayName;
        bp.category = category;
        bp.maxHealth = hp;
        bp.size = size;
        bp.costWood = wood;
        bp.costStone = stone;
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabFolder + "/" + go.name + ".prefab");
        Object.DestroyImmediate(go);
        BuildPiece result = prefab.GetComponent<BuildPiece>();
        catalog.Add(result);
        return result;
    }

    // Поставить деталь в сцену (связанную с префабом)
    static BuildPiece Put(BuildPiece prefab, float x, float y, float z, float yaw)
    {
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
        go.transform.SetParent(root, false);
        go.transform.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0f, yaw, 0f));
        return go.GetComponent<BuildPiece>();
    }

    // Поставить деталь относительно «начала» дома, повёрнутого на houseYaw
    static void PutLocal(BuildPiece prefab, Vector3 origin, float houseYaw, float lx, float ly, float lz, float localYaw)
    {
        Vector3 p = origin + Quaternion.Euler(0f, houseYaw, 0f) * new Vector3(lx, ly, lz);
        Put(prefab, p.x, p.y, p.z, houseYaw + localYaw);
    }

    // Дом 8 x 4 м, собранный из деталей конструктора
    static void BuildHouse(float x, float z, float yaw)
    {
        Transform keep = root;
        root = new GameObject("House").transform;
        root.SetParent(keep, false);
        Vector3 o = new Vector3(x, 0f, z);
        PutLocal(pPlankDoor,   o, yaw, -2f, 0f, -2f, 0f);   // перед: дверь
        PutLocal(pPlankWindow, o, yaw,  2f, 0f, -2f, 0f);   // перед: окно
        PutLocal(pPlankWindow, o, yaw, -2f, 0f,  2f, 0f);   // зад: окно
        PutLocal(pPlank,       o, yaw,  2f, 0f,  2f, 0f);   // зад: стена
        PutLocal(pPlank,       o, yaw, -4f, 0f,  0f, 90f);  // бока
        PutLocal(pPlank,       o, yaw,  4f, 0f,  0f, 90f);
        PutLocal(pRoof,        o, yaw, -2f, 3f, -1f, 0f);   // крыша: два ската с каждой стороны
        PutLocal(pRoof,        o, yaw,  2f, 3f, -1f, 0f);
        PutLocal(pRoof,        o, yaw, -2f, 3f,  1f, 180f);
        PutLocal(pRoof,        o, yaw,  2f, 3f,  1f, 180f);
        PutLocal(pGable,       o, yaw, -4f, 3f,  0f, 90f);  // фронтоны
        PutLocal(pGable,       o, yaw,  4f, 3f,  0f, 90f);
        root = keep;
    }

    // ================= Форт =================
    static void BuildFort()
    {
        root = new GameObject("Fort").transform;
        float h = FortHalf;

        for (int i = -6; i <= 6; i++)
        {
            float c = i * 4f;
            // Север (к тьме) — камень: стены, бойницы, арка-ворота
            Put(i == 0 ? pStoneArch : (Mathf.Abs(i) % 2 == 1 ? pStoneSlit : pStone), c, 0f, h, 0f);
            // Юг (к деревне), запад, восток — частокол
            Put(i == 0 ? pPalisadeGate : pPalisade, c, 0f, -h, 180f);
            Put(pPalisade, -h, 0f, c, 90f);
            Put(pPalisade, h, 0f, c, 90f);
        }

        // Башни: по углам и у северных ворот
        foreach (float x in new[] { -h, h })
            foreach (float z in new[] { -h, h })
                Put(pTower, x, 0f, z, 0f);
        Put(pTower, -6f, 0f, h + 2.5f, 0f);
        Put(pTower, 6f, 0f, h + 2.5f, 0f);

        // Помосты вдоль стен
        float innerN = h - 0.5f - 2f;   // каменная стена толще
        float inner = h - 0.25f - 2f;
        for (int i = -5; i <= 5; i++)
        {
            float c = i * 4f;
            if (i != 0)
            {
                Put(pFloor, c, FloorY, innerN, 0f);
                Put(pFloor, c, FloorY, -inner, 0f);
            }
            Put(pFloor, -inner, FloorY, c, 0f);
            Put(pFloor, inner, FloorY, c, 0f);
        }

        // Лестницы на помосты
        Put(pStairs, -10f, 0f, innerN - 4f, 0f);
        Put(pStairs, 10f, 0f, -inner + 4f, 180f);
        Put(pStairs, inner - 4f, 0f, 16f, 90f);
        Put(pStairs, -inner + 4f, 0f, 16f, 270f);

        // Казармы и склад во дворе — тоже из деталей
        BuildHouse(-17f, -8f, 90f);
        BuildHouse(-17f, 4f, 90f);
        BuildHouse(17f, -8f, 90f);
        BuildHouse(17f, 4f, 90f);

        // Баррикады у северных ворот изнутри и колья снаружи
        Put(pBarricade, -6f, 0f, 14f, 0f);
        Put(pBarricade, 6f, 0f, 14f, 0f);
        for (int i = -2; i <= 2; i++)
            Put(pBarricade, i * 5f, 0f, h + 9f - Mathf.Abs(i) * 1.5f, 0f);

        // Декор (не разбирается): кристалл лайлилиума, костёр, ящики
        root = new GameObject("Fort_Decor").transform;
        GameObject cr = Box("Lailillium_Crystal", new Vector3(0f, 1.4f, -4f), new Vector3(1f, 2.8f, 1f), crystal);
        cr.transform.rotation = Quaternion.Euler(0f, 45f, 8f);
        Box("Crystal_Base", new Vector3(0f, 0.2f, -4f), new Vector3(3f, 0.4f, 3f), stone);
        GameObject lg = new GameObject("Crystal_Light");
        lg.transform.position = new Vector3(0f, 2.5f, -4f);
        lg.transform.SetParent(root, true);
        Light l = lg.AddComponent<Light>();
        l.type = LightType.Point; l.color = new Color(0.65f, 0.4f, 1f); l.range = 12f; l.intensity = 4f;

        Box("Campfire", new Vector3(-6f, 0.25f, -16f), new Vector3(1.4f, 0.5f, 1.4f), stone);
        for (int i = 0; i < 6; i++)
            Box("Crate", new Vector3(-12f + (i % 3) * 1.1f, 0.5f + (i / 3) * 1f, -20f), Vector3.one, darkWood);
    }

    // ================= Деревня (юг) =================
    static void BuildVillage()
    {
        root = new GameObject("Village").transform;
        Vector3[] spots =
        {
            new Vector3(-20, -48, 0), new Vector3(-6, -50, 90), new Vector3(8, -47, 0), new Vector3(22, -52, 90),
            new Vector3(-24, -66, 90), new Vector3(-10, -68, 0), new Vector3(5, -66, 0), new Vector3(19, -70, 90),
            new Vector3(-4, -84, 0), new Vector3(12, -86, 0),
        };
        foreach (Vector3 s in spots) BuildHouse(s.x, s.y, s.z);

        root = new GameObject("Village_Decor").transform;
        GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = "Airship_LandingPad";
        pad.transform.SetParent(root, false);
        pad.transform.position = new Vector3(45f, 0.1f, -40f);
        pad.transform.localScale = new Vector3(16f, 0.1f, 16f);
        pad.GetComponent<Renderer>().sharedMaterial = darkWood;
        pad.isStatic = true;
    }

    // ================= Лес (север, зона вылазок) =================
    static void BuildForest()
    {
        root = new GameObject("Forest").transform;
        Random.State saved = Random.state;
        Random.InitState(1234);
        int placed = 0, tries = 0;
        while (placed < 160 && tries < 3000)
        {
            tries++;
            Vector3 p = new Vector3(Random.Range(-190f, 190f), 0f, Random.Range(45f, 185f));
            if (Mathf.Abs(p.x) < 7f && p.z < 100f) continue; // тропа от ворот
            float s = Random.Range(0.8f, 1.5f);

            GameObject tree = new GameObject("Tree");
            tree.transform.SetParent(root, false);
            tree.transform.position = p;
            tree.isStatic = true;
            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0f, 2f * s, 0f);
            trunk.transform.localScale = new Vector3(0.5f * s, 2f * s, 0.5f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = darkWood;
            trunk.isStatic = true;
            GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            crown.transform.SetParent(tree.transform, false);
            crown.transform.localPosition = new Vector3(0f, 5f * s, 0f);
            crown.transform.localScale = Vector3.one * 3.5f * s;
            crown.GetComponent<Renderer>().sharedMaterial = leaves;
            Object.DestroyImmediate(crown.GetComponent<Collider>());
            crown.isStatic = true;
            placed++;
        }
        Random.state = saved;
    }

    // ================= Граница тьмы и края карты =================
    static void BuildDarkness()
    {
        root = new GameObject("Darkness").transform;
        GameObject wall = Box("Darkness_Wall", new Vector3(0f, 40f, 196f), new Vector3(400f, 80f, 2f), dark);
        Object.DestroyImmediate(wall.GetComponent<Collider>());
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0f, 10f, 200f),  new Vector3(400f, 20f, 1f)),
            (new Vector3(0f, 10f, -200f), new Vector3(400f, 20f, 1f)),
            (new Vector3(200f, 10f, 0f),  new Vector3(1f, 20f, 400f)),
            (new Vector3(-200f, 10f, 0f), new Vector3(1f, 20f, 400f)),
        })
        {
            GameObject b = Box("MapBorder", pos, size, dark);
            Object.DestroyImmediate(b.GetComponent<Renderer>());
            Object.DestroyImmediate(b.GetComponent<MeshFilter>());
        }
    }

    // ================= Герой =================
    static void BuildPlayer()
    {
        GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.transform.position = new Vector3(0f, 1f, -12f);
        player.GetComponent<Renderer>().sharedMaterial = playerMat;
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
        cam.farClipPlane = 500f;
        EnableAntiAliasing(cam);
        pivot.AddComponent<PlayerCamera>();
        cam.gameObject.AddComponent<FpsCounter>();

        // Меч и молот (пока из кубиков) — в правой руке
        GameObject sword = new GameObject("Sword");
        sword.transform.SetParent(pivot.transform, false);
        sword.transform.localPosition = new Vector3(0.45f, -0.35f, 0.55f);
        sword.transform.localRotation = Quaternion.Euler(60f, 0f, 0f);
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, 0.45f, 0f), new Vector3(0.06f, 0.8f, 0.02f), steel, Vector3.zero, false);
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0f), new Vector3(0.25f, 0.04f, 0.05f), darkWood, Vector3.zero, false);
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, -0.08f, 0f), new Vector3(0.04f, 0.2f, 0.04f), darkWood, Vector3.zero, false);

        GameObject hammer = new GameObject("Hammer");
        hammer.transform.SetParent(pivot.transform, false);
        hammer.transform.localPosition = new Vector3(0.45f, -0.4f, 0.55f);
        hammer.transform.localRotation = Quaternion.Euler(50f, 0f, 0f);
        Part(hammer.transform, PrimitiveType.Cube, new Vector3(0f, 0.2f, 0f), new Vector3(0.05f, 0.55f, 0.05f), darkWood, Vector3.zero, false);
        Part(hammer.transform, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), new Vector3(0.22f, 0.12f, 0.12f), steel, Vector3.zero, false);

        foreach (GameObject tool in new[] { sword, hammer })
            foreach (Renderer r in tool.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        PlayerHotbar hotbar = player.AddComponent<PlayerHotbar>();
        hotbar.swordModel = sword.transform;
        hotbar.hammerModel = hammer.transform;

        player.AddComponent<Inventory>();
        player.AddComponent<PlayerCombat>();

        PlayerBuilder builder = player.AddComponent<PlayerBuilder>();
        builder.ghostMaterial = ghost;
        builder.pieces = new List<BuildPiece>(catalog);
    }

    // ================= Атмосфера =================
    static void SetupAtmosphere()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.42f, 0.44f, 0.48f);
        RenderSettings.fogDensity = 0.006f;
        Light sun = Object.FindFirstObjectByType<Light>();
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            sun.color = new Color(1f, 0.92f, 0.82f);
            sun.intensity = 1.1f;
        }
    }

    // Сглаживание «лесенки» на краях (SMAA)
    static void EnableAntiAliasing(Camera cam)
    {
        System.Type t = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        if (t == null) return;
        Component data = cam.GetComponent(t);
        if (data == null) data = cam.gameObject.AddComponent(t);
        var so = new SerializedObject(data);
        SerializedProperty aa = so.FindProperty("m_Antialiasing");
        SerializedProperty q = so.FindProperty("m_AntialiasingQuality");
        if (aa != null) aa.intValue = 2;
        if (q != null) q.intValue = 2;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ================= Помощники =================
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

    // Полупрозрачный материал для «призрака» в режиме стройки
    static Material GhostMat()
    {
        Material m = Mat("Ghost", new Color(0.3f, 1f, 0.4f, 0.4f));
        if (m.HasProperty("_Surface"))
        {
            m.SetFloat("_Surface", 1f); // Transparent
            m.SetFloat("_Blend", 0f);   // Alpha
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        EditorUtility.SetDirty(m);
        return m;
    }
}
