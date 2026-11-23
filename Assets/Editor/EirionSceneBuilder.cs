using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// Скрипт ДЛЯ РЕДАКТОРА (не для игры). Лежит в папке Assets/Editor.
// Меню "Eirion -> Создать сцену форта":
//   1) делает детали построек и станки (префабы) в Assets/Prefabs/Building;
//   2) строит огромный форт на каменном плато, под плато — подземелье-лабиринт;
//   3) ставит деревню, лес, камни, кристаллы лайлилиума;
//   4) на севере — пропасть Раскола: туман, из которого лезут твари, вдали вторая половина мира,
//      внизу светится ядро планеты;
//   5) делает сетевого игрока (префаб Assets/Prefabs/Player) и меню «одному / с друзьями».
// Направления: север (+Z) — к пропасти, юг (-Z) — к деревне.
public static class EirionSceneBuilder
{
    const string ScenePath = "Assets/Scenes/Fort.unity";
    const string PrefabFolder = "Assets/Prefabs/Building";

    // Плато форта: квадрат 96 x 96 м, высота 4 м, собран из клеток 6 x 6 м
    const float Top = 4f;          // высота двора форта
    const float Cell = 6f;
    const int Cells = 16;
    const float PlateauHalf = 48f;
    const float WallLine = 44f;    // где стоят внешние стены
    const float Rampart = 2.7f;    // высота помостов над двором
    const float AbyssEdge = 180f;  // где начинается пропасть

    static Transform root;

    // Материалы
    static Material grass, wood, darkWood, stone, roof, leaves, playerMat, crystal, dark, steel, ghost,
                    fortRock, dungeonFloor, abyssRock, rockMat, iron, cloth;

    // Детали конструктора
    static BuildPiece pPalisade, pPalisadeGate, pPlank, pPlankHalf, pPlankWindow, pPlankDoor,
                      pFloor, pStairs, pPillar, pRoof, pGable, pBarricade,
                      pStone, pStoneHalf, pStoneSlit, pStoneArch, pTower,
                      pWorkbench, pStonecutter,
                      pStoneWallPlain, pStoneFloor, pStoneStairs, pStoneParapet, pStoneColumn;
    static List<BuildPiece> catalog;
    static int sceneIdCounter;
    static int nodeIdCounter;
    static bool markVillage; // детали, которые ставим сейчас, — часть деревни
    static Material torchFlame;

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

        // Земля: от южного края карты до края пропасти
        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Plane);
        g.name = "Ground";
        g.transform.position = new Vector3(0f, 0f, (AbyssEdge - 300f) / 2f);
        g.transform.localScale = new Vector3(60f, 1f, (AbyssEdge + 300f) / 10f); // 600 x 480 метров
        g.GetComponent<Renderer>().sharedMaterial = grass;
        g.isStatic = true;

        sceneIdCounter = 0;
        nodeIdCounter = 0;
        markVillage = false;
        torchFlame = Mat("TorchFlame", new Color(1f, 0.6f, 0.2f), new Color(1f, 0.5f, 0.15f) * 4f);

        bool[,] open = BuildPlateauAndDungeon();
        BuildFort();
        BuildVillage();
        BuildForest();
        BuildRocks();
        BuildLailillium(open);
        BuildAbyss();
        BuildBorders();
        SetupCamera();
        GameObject playerPrefab = BuildPlayerPrefab();
        SetupNetwork(playerPrefab);
        SetupPlayerSettings();

        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path != ScenePath && !s.path.EndsWith("TestScene.unity")) scenes.Add(s);
        EditorBuildSettings.scenes = scenes.ToArray();

        EditorUtility.DisplayDialog("Eirion", "Готово! Сцена Fort создана и сохранена.\nНажми ▶ Play, потом «Играть одному».", "Ок");
    }

    // ================= Материалы (тёмные тона) =================
    static void MakeMaterials()
    {
        grass    = Mat("Grass",    new Color(0.19f, 0.22f, 0.16f));
        wood     = Mat("Wood",     new Color(0.36f, 0.26f, 0.16f));
        darkWood = Mat("DarkWood", new Color(0.22f, 0.15f, 0.09f));
        stone    = Mat("Stone",    new Color(0.38f, 0.38f, 0.40f));
        roof     = Mat("Roof",     new Color(0.27f, 0.12f, 0.10f));
        leaves   = Mat("Leaves",   new Color(0.11f, 0.18f, 0.10f));
        playerMat= Mat("Player",   new Color(0.70f, 0.25f, 0.20f));
        steel    = Mat("Steel",    new Color(0.75f, 0.77f, 0.8f));
        iron     = Mat("Iron",     new Color(0.18f, 0.18f, 0.2f));
        cloth    = Mat("Banner",   new Color(0.35f, 0.06f, 0.06f));
        crystal  = Mat("Lailillium", new Color(0.55f, 0.35f, 1f), new Color(0.6f, 0.35f, 1f) * 3f);
        dark     = Mat("Darkness", new Color(0.05f, 0.02f, 0.08f), new Color(0.25f, 0.05f, 0.35f));
        fortRock = Mat("FortRock", new Color(0.27f, 0.26f, 0.27f));
        dungeonFloor = Mat("DungeonFloor", new Color(0.17f, 0.16f, 0.16f));
        abyssRock = Mat("AbyssRock", new Color(0.08f, 0.07f, 0.09f));
        rockMat  = Mat("Rock",     new Color(0.33f, 0.33f, 0.35f));
        ghost    = GhostMat();
    }

    // ================= Детали построек =================
    // Брёвна — частокол, ворота, баррикада, столб: строятся где угодно.
    // Доски — нужен верстак рядом. Каменные блоки — нужен каменотёс рядом.
    static void MakePiecePrefabs()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Prefabs", "Building");
        catalog = new List<BuildPiece>();
        GameObject go;
        const ResourceType L = ResourceType.Wood, P = ResourceType.Plank, S = ResourceType.Stone, B = ResourceType.StoneBlock;
        const StationType none = StationType.None, bench = StationType.Workbench, cutter = StationType.Stonecutter;

        // ---------- СТАНКИ ----------
        // Верстак: стол с инструментами. Из брёвен делает доски.
        go = new GameObject("Workbench");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.85f, 0f), new Vector3(2.2f, 0.15f, 1.1f), wood);
        foreach (float x in new[] { -0.95f, 0.95f })
            foreach (float z in new[] { -0.4f, 0.4f })
                Part(go.transform, PrimitiveType.Cube, new Vector3(x, 0.4f, z), new Vector3(0.12f, 0.8f, 0.12f), darkWood, Vector3.zero, false);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-0.5f, 0.98f, 0.1f), new Vector3(0.6f, 0.1f, 0.25f), wood, new Vector3(0f, 20f, 0f), false);   // доска
        Part(go.transform, PrimitiveType.Cube, new Vector3(0.4f, 0.96f, -0.2f), new Vector3(0.5f, 0.04f, 0.12f), steel, new Vector3(0f, -10f, 0f), false); // пила
        Part(go.transform, PrimitiveType.Cylinder, new Vector3(0.8f, 1.0f, 0.25f), new Vector3(0.12f, 0.06f, 0.12f), iron, Vector3.zero, false);
        pWorkbench = SavePiece(go, "Верстак", "Станки", 300f, new Vector3(2.2f, 1.1f, 1.1f), none, (L, 6));

        // Каменотёс: каменная плита, точильный круг. Из камня делает блоки.
        go = new GameObject("Stonecutter");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.45f, 0f), new Vector3(2f, 0.9f, 1.2f), stone);
        Part(go.transform, PrimitiveType.Cylinder, new Vector3(0.55f, 1.25f, 0f), new Vector3(0.9f, 0.08f, 0.9f), rockMat, new Vector3(90f, 0f, 0f), false);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0.55f, 1.0f, 0f), new Vector3(0.12f, 0.3f, 0.5f), darkWood, Vector3.zero, false);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-0.5f, 1.05f, 0.1f), new Vector3(0.5f, 0.3f, 0.4f), rockMat, new Vector3(0f, 15f, 0f), false);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-0.2f, 0.93f, -0.35f), new Vector3(0.08f, 0.06f, 0.45f), iron, Vector3.zero, false);
        pStonecutter = SavePiece(go, "Каменотёс", "Станки", 600f, new Vector3(2f, 1.7f, 1.2f), bench, (S, 8), (P, 4));

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
        pPalisade = SavePiece(go, "Частокол", "Дерево", 300f, new Vector3(4f, 4f, 0.5f), none, (L, 4));

        // Ворота частокола (проход 2.4 x 3.3 м)
        go = new GameObject("WoodPalisadeGate");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.6f, 2.25f, 0f), new Vector3(0.8f, 4.5f, 0.8f), darkWood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.6f, 2.25f, 0f), new Vector3(0.8f, 4.5f, 0.8f), darkWood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.9f, 0f), new Vector3(2.4f, 1.2f, 0.6f), wood);
        pPalisadeGate = SavePiece(go, "Ворота частокола", "Дерево", 450f, new Vector3(4f, 4.5f, 0.8f), none, (L, 6));

        // Баррикада с кольями
        go = new GameObject("Barricade");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), new Vector3(4f, 0.5f, 0.5f), darkWood);
        for (int i = 0; i < 5; i++)
            Part(go.transform, PrimitiveType.Cube, new Vector3(-1.6f + i * 0.8f, 0.8f, 0.3f), new Vector3(0.15f, 0.15f, 2f), wood, new Vector3(-40f, 0f, 0f));
        pBarricade = SavePiece(go, "Баррикада", "Дерево", 150f, new Vector3(4f, 1.5f, 1.5f), none, (L, 3));

        // Столб 3 м
        go = new GameObject("WoodPillar");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(0.5f, 3f, 0.5f), darkWood);
        pPillar = SavePiece(go, "Столб", "Дерево", 150f, new Vector3(0.5f, 3f, 0.5f), none, (L, 1));

        // Стена из досок 4 x 3 м
        go = new GameObject("PlankWall");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 0.3f), wood);
        Frame(go, 3f);
        pPlank = SavePiece(go, "Стена из досок", "Дерево", 250f, new Vector3(4f, 3f, 0.3f), bench, (P, 4));

        // Полустена 4 x 1.5 м
        go = new GameObject("PlankWallHalf");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.75f, 0f), new Vector3(4f, 1.5f, 0.3f), wood);
        Frame(go, 1.5f);
        pPlankHalf = SavePiece(go, "Полустена", "Дерево", 150f, new Vector3(4f, 1.5f, 0.3f), bench, (P, 2));

        // Стена с окном (окно 1.2 x 1 м)
        go = new GameObject("PlankWallWindow");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2.6f, 0f), new Vector3(4f, 0.8f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.3f, 1.7f, 0f), new Vector3(1.4f, 1f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.3f, 1.7f, 0f), new Vector3(1.4f, 1f, 0.3f), wood);
        Frame(go, 3f);
        pPlankWindow = SavePiece(go, "Стена с окном", "Дерево", 220f, new Vector3(4f, 3f, 0.3f), bench, (P, 4));

        // Стена с дверным проёмом (1.4 x 2.2 м)
        go = new GameObject("PlankWallDoor");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.35f, 1.5f, 0f), new Vector3(1.3f, 3f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.35f, 1.5f, 0f), new Vector3(1.3f, 3f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2.6f, 0f), new Vector3(1.4f, 0.8f, 0.3f), wood);
        Frame(go, 3f);
        pPlankDoor = SavePiece(go, "Стена с дверью", "Дерево", 220f, new Vector3(4f, 3f, 0.3f), bench, (P, 4));

        // Пол / помост 4 x 4 м
        go = new GameObject("WoodFloor");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.15f, 0f), new Vector3(4f, 0.3f, 4f), wood);
        pFloor = SavePiece(go, "Пол / помост", "Дерево", 200f, new Vector3(4f, 0.3f, 4f), bench, (P, 4));

        // Лестница: +3 м на длине 4 м
        go = new GameObject("WoodStairs");
        float sa = Mathf.Atan2(3f, 4f) * Mathf.Rad2Deg;
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(2f, 0.3f, 5f), wood, new Vector3(-sa, 0f, 0f));
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 1.9f), new Vector3(1.8f, 3f, 0.2f), darkWood, Vector3.zero, false);
        pStairs = SavePiece(go, "Лестница", "Дерево", 200f, new Vector3(2f, 3f, 4f), bench, (P, 4));

        // Скат крыши: 4 м в ширину, поднимается на 2 м на длине 2 м (45°)
        go = new GameObject("WoodRoof");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(4.2f, 0.15f, 2.95f), roof, new Vector3(-45f, 0f, 0f));
        pRoof = SavePiece(go, "Скат крыши", "Дерево", 150f, new Vector3(4f, 2f, 2f), bench, (P, 3));

        // Фронтон — треугольник под крышей, 4 x 2 м
        go = new GameObject("WoodGable");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.335f, 0f), new Vector3(4f, 0.67f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.0f, 0f), new Vector3(2.67f, 0.67f, 0.3f), wood);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1.665f, 0f), new Vector3(1.33f, 0.67f, 0.3f), wood);
        pGable = SavePiece(go, "Фронтон", "Дерево", 120f, new Vector3(4f, 2f, 0.3f), bench, (P, 2));

        // ---------- КАМЕНЬ ----------
        // Каменная стена 4 x 4 м с зубцами
        go = new GameObject("StoneWall");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 1f), stone);
        Crenels(go, 4f);
        pStone = SavePiece(go, "Каменная стена", "Камень", 900f, new Vector3(4f, 4.6f, 1f), cutter, (B, 6));

        // Каменная полустена 4 x 2 м
        go = new GameObject("StoneWallHalf");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(4f, 2f, 1f), stone);
        pStoneHalf = SavePiece(go, "Каменная полустена", "Камень", 500f, new Vector3(4f, 2f, 1f), cutter, (B, 3));

        // Стена с бойницей (узкая щель для лука)
        go = new GameObject("StoneWallSlit");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.1f, 2f, 0f), new Vector3(1.8f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.1f, 2f, 0f), new Vector3(1.8f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(0.4f, 1.2f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.4f, 0f), new Vector3(0.4f, 1.2f, 1f), stone);
        Crenels(go, 4f);
        pStoneSlit = SavePiece(go, "Стена с бойницей", "Камень", 850f, new Vector3(4f, 4.6f, 1f), cutter, (B, 6));

        // Каменная арка (проход 2 x 3 м)
        go = new GameObject("StoneArch");
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.5f, 2f, 0f), new Vector3(1f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.5f, 2f, 0f), new Vector3(1f, 4f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 3.5f, 0f), new Vector3(2f, 1f, 1f), stone);
        Crenels(go, 4f);
        pStoneArch = SavePiece(go, "Каменная арка", "Камень", 800f, new Vector3(4f, 4.6f, 1f), cutter, (B, 6));

        // Башня 4 x 4 x 8 м
        go = new GameObject("StoneTower");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 4f, 0f), new Vector3(4f, 8f, 4f), stone);
        foreach (float x in new[] { -1.5f, 1.5f })
            foreach (float z in new[] { -1.5f, 1.5f })
                Part(go.transform, PrimitiveType.Cube, new Vector3(x, 8.35f, z), new Vector3(1f, 0.7f, 1f), stone);
        pTower = SavePiece(go, "Башня", "Камень", 1500f, new Vector3(4f, 8.7f, 4f), cutter, (B, 20), (P, 4));

        // Гладкая каменная кладка 4 x 4 м (для этажей замка)
        go = new GameObject("StoneWallPlain");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 1f), stone);
        pStoneWallPlain = SavePiece(go, "Каменная кладка", "Камень", 900f, new Vector3(4f, 4f, 1f), cutter, (B, 5));

        // Каменное перекрытие 4 x 4 м (пол этажа, крыша, мост)
        go = new GameObject("StoneFloor");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.125f, 0f), new Vector3(4f, 0.25f, 4f), stone);
        pStoneFloor = SavePiece(go, "Каменное перекрытие", "Камень", 600f, new Vector3(4f, 0.25f, 4f), cutter, (B, 4));

        // Каменная лестница: +4 м (целый этаж) на длине 8 м
        go = new GameObject("StoneStairs");
        float ssa = Mathf.Atan2(4f, 8f) * Mathf.Rad2Deg;
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2f, 0f), new Vector3(2f, 0.35f, Mathf.Sqrt(80f)), stone, new Vector3(-ssa, 0f, 0f));
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 2f), new Vector3(1.9f, 2f, 4f), fortRock, Vector3.zero, false);
        pStoneStairs = SavePiece(go, "Каменная лестница", "Камень", 600f, new Vector3(2f, 4f, 8f), cutter, (B, 6));

        // Зубцы (парапет) 4 м — на крыши и стены, за ними прячутся от тварей
        go = new GameObject("StoneParapet");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), new Vector3(4f, 1f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(-1.25f, 1.35f, 0f), new Vector3(1f, 0.7f, 1f), stone);
        Part(go.transform, PrimitiveType.Cube, new Vector3(1.25f, 1.35f, 0f), new Vector3(1f, 0.7f, 1f), stone);
        pStoneParapet = SavePiece(go, "Зубцы", "Камень", 500f, new Vector3(4f, 1.7f, 1f), cutter, (B, 3));

        // Каменная колонна 1 x 4 м
        go = new GameObject("StoneColumn");
        Part(go.transform, PrimitiveType.Cube, new Vector3(0f, 2f, 0f), new Vector3(1.1f, 4f, 1.1f), stone);
        pStoneColumn = SavePiece(go, "Каменная колонна", "Камень", 400f, new Vector3(1.1f, 4f, 1.1f), cutter, (B, 2));

        // Станки — в конце меню (вкладка «Станки»)
        catalog.Remove(pWorkbench); catalog.Remove(pStonecutter);
        catalog.Add(pWorkbench); catalog.Add(pStonecutter);
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

    static BuildPiece SavePiece(GameObject go, string displayName, string category, float hp, Vector3 size,
                                StationType needs, params (ResourceType type, int amount)[] cost)
    {
        BuildPiece bp = go.AddComponent<BuildPiece>();
        bp.pieceId = go.name;
        bp.displayName = displayName;
        bp.category = category;
        bp.maxHealth = hp;
        bp.size = size;
        bp.requiredStation = needs;
        bp.costTypes = new ResourceType[cost.Length];
        bp.costAmounts = new int[cost.Length];
        for (int i = 0; i < cost.Length; i++) { bp.costTypes[i] = cost[i].type; bp.costAmounts[i] = cost[i].amount; }
        if (go.name == "Workbench") bp.station = StationType.Workbench;
        if (go.name == "Stonecutter") bp.station = StationType.Stonecutter;
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
        BuildPiece bp = go.GetComponent<BuildPiece>();
        bp.netId = ++sceneIdCounter; // номер детали — одинаковый у всех игроков
        if (markVillage) { bp.village = true; bp.locked = true; }
        PrefabUtility.RecordPrefabInstancePropertyModifications(bp);
        return bp;
    }

    // Поставить деталь относительно «начала» дома, повёрнутого на houseYaw
    static void PutLocal(BuildPiece prefab, Vector3 origin, float houseYaw, float lx, float ly, float lz, float localYaw)
    {
        Vector3 p = origin + Quaternion.Euler(0f, houseYaw, 0f) * new Vector3(lx, ly, lz);
        Put(prefab, p.x, p.y, p.z, houseYaw + localYaw);
    }

    // Дом 8 x 4 м, собранный из деталей конструктора
    static void BuildHouse(float x, float y, float z, float yaw)
    {
        Transform keep = root;
        root = new GameObject("House").transform;
        root.SetParent(keep, false);
        Vector3 o = new Vector3(x, y, z);
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

    // ================= Плато и подземелье =================
    // Клетка (i, j): x от -48 + 6i до -42 + 6i, z так же по j.
    static float CellX(int i) => -PlateauHalf + Cell * (i + 0.5f);

    // Лестницы из двора вниз: комната + коридор за ней. topZ — где вход сверху.
    static readonly (int i, int j, int wi, int wj)[] Stairwells =
    {
        (3, 7, 3, 8),    // в донжоне
        (11, 7, 11, 8),  // в большом зале
    };

    static bool[,] BuildPlateauAndDungeon()
    {
        root = new GameObject("Plateau").transform;
        bool[,] open = new bool[Cells, Cells];

        // Лабиринт: комнаты на нечётных клетках, проходы между ними (обход в глубину)
        var rnd = new System.Random(2024);
        const int R = 7; // 7 x 7 комнат
        bool[,] seen = new bool[R, R];
        var stack = new Stack<(int, int)>();
        stack.Push((3, 0));
        seen[3, 0] = true;
        open[7, 1] = true;
        int[] dx = { 1, -1, 0, 0 }, dz = { 0, 0, 1, -1 };
        while (stack.Count > 0)
        {
            var (a, b) = stack.Peek();
            var options = new List<int>();
            for (int d = 0; d < 4; d++)
            {
                int na = a + dx[d], nb = b + dz[d];
                if (na >= 0 && na < R && nb >= 0 && nb < R && !seen[na, nb]) options.Add(d);
            }
            if (options.Count == 0) { stack.Pop(); continue; }
            int dir = options[rnd.Next(options.Count)];
            int ta = a + dx[dir], tb = b + dz[dir];
            seen[ta, tb] = true;
            open[2 * ta + 1, 2 * tb + 1] = true;
            open[2 * a + 1 + dx[dir], 2 * b + 1 + dz[dir]] = true; // стенка между комнатами
            stack.Push((ta, tb));
        }
        // Несколько лишних проходов — чтобы были кольца и было где заблудиться
        for (int k = 0; k < 7; k++)
        {
            int i = 1 + rnd.Next(13), j = 1 + rnd.Next(13);
            if ((i + j) % 2 == 1) open[i, j] = true;
        }
        // Лестницы вниз и входы снаружи (запад и восток)
        foreach (var s in Stairwells) { open[s.i, s.j] = true; open[s.wi, s.wj] = true; open[s.wi, 2 * s.wj - s.j] = true; }
        open[0, 7] = true;
        open[14, 7] = true; open[15, 7] = true;

        var holes = new HashSet<(int, int)>();
        foreach (var s in Stairwells) { holes.Add((s.i, s.j)); holes.Add((s.wi, s.wj)); }

        // Пол подземелья
        Box("Dungeon_Floor", new Vector3(0f, 0.025f, 0f), new Vector3(PlateauHalf * 2f, 0.05f, PlateauHalf * 2f), dungeonFloor);

        // Сплошные клетки — скала, открытые — коридор с потолком
        for (int i = 0; i < Cells; i++)
            for (int j = 0; j < Cells; j++)
            {
                Vector3 c = new Vector3(CellX(i), 0f, CellX(j));
                if (!open[i, j])
                    Box("Rock", c + Vector3.up * (Top / 2f), new Vector3(Cell, Top, Cell), fortRock);
                else if (!holes.Contains((i, j)))
                    Box("Ceiling", c + Vector3.up * (Top - 0.25f), new Vector3(Cell, 0.5f, Cell), fortRock);
            }

        // Лестницы-пандусы из двора вниз + перила вокруг дыры
        float ang = Mathf.Atan2(Top, Cell * 2f) * Mathf.Rad2Deg;
        float len = Mathf.Sqrt(Top * Top + Cell * Cell * 4f);
        foreach (var s in Stairwells)
        {
            float x = CellX(s.i);
            float zTop = CellX(s.j) - (s.wj - s.j) * Cell / 2f;  // край комнаты со стороны двора
            float zBot = CellX(s.wj) + (s.wj - s.j) * Cell / 2f; // дальний край коридора
            float zc = (zTop + zBot) / 2f;
            float sign = Mathf.Sign(zBot - zTop);
            GameObject ramp = Box("Dungeon_Stairs", new Vector3(x, Top / 2f - 0.28f, zc), new Vector3(Cell - 0.2f, 0.5f, len), dungeonFloor);
            ramp.transform.rotation = Quaternion.Euler(sign * ang, 0f, 0f);
            // перила по бокам и на дальнем краю
            Box("Rail", new Vector3(x - Cell / 2f - 0.15f, Top + 0.4f, zc), new Vector3(0.3f, 0.8f, Cell * 2f), fortRock);
            Box("Rail", new Vector3(x + Cell / 2f + 0.15f, Top + 0.4f, zc), new Vector3(0.3f, 0.8f, Cell * 2f), fortRock);
            Box("Rail", new Vector3(x, Top + 0.4f, zBot + sign * 0.15f), new Vector3(Cell + 0.6f, 0.8f, 0.3f), fortRock);
            MakeTorch(new Vector3(x - Cell / 2f - 0.6f, Top, zTop - sign * 0.8f));
        }

        // Подземелье: факелы (горят всегда), кости, ящики, решётки
        root = new GameObject("Dungeon").transform;
        Material bone = Mat("Bone", new Color(0.72f, 0.68f, 0.58f));
        int t = 0;
        for (int i = 1; i < Cells - 1; i += 2)
            for (int j = 1; j < Cells - 1; j += 2)
            {
                if (!open[i, j] || holes.Contains((i, j))) continue;
                Vector3 c = new Vector3(CellX(i), 0f, CellX(j));
                t++;
                if (t % 3 == 0) MakeTorch(c + new Vector3(2.3f, 0f, 2.3f), true);
                if (t % 4 == 1)
                    for (int k = 0; k < 4; k++)
                        Box("Bone", c + new Vector3(-1.5f + k * 0.4f, 0.1f, -2f + (k % 2) * 0.3f), new Vector3(0.5f, 0.08f, 0.08f), bone)
                            .transform.rotation = Quaternion.Euler(0f, k * 47f, 0f);
                if (t % 5 == 2)
                    for (int k = 0; k < 3; k++)
                        Box("Crate", c + new Vector3(-2.2f, 0.5f + (k / 2) * 1f, 2.2f - (k % 2) * 1.05f), Vector3.one, darkWood);
            }
        // Входы снаружи: факелы и старые решётки сверху
        foreach (float sx in new[] { -1f, 1f })
        {
            float ex = sx * (PlateauHalf + 1.5f);
            MakeTorch(new Vector3(ex, 0f, CellX(7) - 3.8f));
            MakeTorch(new Vector3(ex, 0f, CellX(7) + 3.8f));
            for (int k = 0; k < 6; k++)
                Box("Bars", new Vector3(sx * (PlateauHalf - 0.2f), Top - 0.9f, CellX(7) - 2.5f + k), new Vector3(0.1f, 1.2f, 0.1f), iron);
        }

        // Пандусы с земли на плато: с севера (откуда идут твари) и с юга (к деревне)
        root = new GameObject("Ramps").transform;
        float rampLen = 14f;
        float rAng = Mathf.Atan2(Top, rampLen) * Mathf.Rad2Deg;
        float rLen = Mathf.Sqrt(Top * Top + rampLen * rampLen);
        foreach (float sz in new[] { 1f, -1f })
        {
            float zc = sz * (PlateauHalf + rampLen / 2f);
            GameObject ramp = Box("Ramp", new Vector3(0f, Top / 2f - 0.26f, zc), new Vector3(10f, 0.5f, rLen + 0.5f), fortRock);
            ramp.transform.rotation = Quaternion.Euler(sz * rAng, 0f, 0f);
            foreach (float sx in new[] { -1f, 1f })
            {
                GameObject side = Box("Ramp_Side", new Vector3(sx * 5.4f, Top / 2f + 0.2f, zc), new Vector3(0.8f, 1.2f, rLen), fortRock);
                side.transform.rotation = Quaternion.Euler(sz * rAng, 0f, 0f);
            }
        }
        return open;
    }

    // ================= Замок на плато =================
    // Высота этажа 4 м. Двор форта (плато) — на высоте 4. Этажи: 4, 8, 12, 16.
    // Внешние стены в два яруса, по ним идёт галерея (на крышах корпусов и на помостах).
    // Внутри — корпуса из каменных деталей с этажами, лестницами, дверями и бойницами,
    // узкая улица с севера на юг (по ней идут твари), мосты между корпусами на втором ярусе.
    const float Floor = 4f;

    static void BuildFort()
    {
        root = new GameObject("Castle").transform;
        float w = WallLine, y = Top, y2 = Top + Floor;

        // ---- Внешняя стена: нижний ярус кладка (ворота на севере и юге), верхний — зубцы ----
        Transform keepRoot = root;
        root = new GameObject("Curtain_Wall").transform;
        root.SetParent(keepRoot, false);
        for (int i = -10; i <= 10; i++)
        {
            float c = i * 4f;
            Put(i == 0 ? pStoneArch : pStoneWallPlain, c, y, w, 0f);
            Put(i == 0 ? pStoneArch : pStoneWallPlain, c, y, -w, 180f);
            Put(pStoneWallPlain, -w, y, c, 90f);
            Put(pStoneWallPlain, w, y, c, 90f);
            Put(pStoneParapet, c, y2, w, 0f);
            Put(pStoneParapet, c, y2, -w, 180f);
            Put(pStoneParapet, -w, y2, c, 90f);
            Put(pStoneParapet, w, y2, c, 90f);
        }
        foreach (float x in new[] { -w, w })
            foreach (float z in new[] { -w, w })
                Put(pTower, x, y, z, 0f);
        foreach (float sz in new[] { 1f, -1f })
        {
            Put(pTower, -6f, y, sz * (w + 2f), 0f);
            Put(pTower, 6f, y, sz * (w + 2f), 0f);
        }
        // Галерея вдоль западной и восточной стены
        for (int k = 0; k < 14; k++)
        {
            Put(pStoneFloor, -w + 2f, y2, -26f + 4f * k, 0f);
            Put(pStoneFloor, w - 2f, y2, -26f + 4f * k, 0f);
        }
        root = keepRoot;

        // ---- Корпуса ----
        // Казармы (северо-запад, к стене) — 1 этаж, крыша = галерея
        Building("Barracks", -44, 28, -4, 44, 1, "NW",
            new[] { ('S', 0, -30f), ('S', 0, -14f), ('E', 0, 30f) }, 0,
            new[] { new Vector2(-42, 28), new Vector2(-22, 28), new Vector2(-4, 34), new Vector2(-4, 38), new Vector2(-4, 42) });
        // Оружейная и мастерская (северо-восток)
        Building("Armory", 4, 28, 44, 44, 1, "NE",
            new[] { ('S', 0, 14f), ('S', 0, 30f), ('W', 0, 30f) }, 1,
            new[] { new Vector2(42, 28), new Vector2(22, 28), new Vector2(4, 34), new Vector2(4, 38), new Vector2(4, 42) });
        // Донжон — главная башня-замок, 3 этажа + крыша
        Building("Donjon", -36, -12, -12, 16, 3, "",
            new[] { ('S', 0, -26f), ('E', 0, 2f), ('N', 1, -22f) }, 0, new Vector2[0]);
        // Большой зал — 2 этажа
        Building("Great_Hall", 12, -12, 36, 16, 2, "",
            new[] { ('W', 0, 2f), ('S', 0, 22f), ('N', 1, 22f) }, 1, new Vector2[0]);
        // Склады и жильё (юг, к стене)
        Building("South_West", -44, -44, -4, -28, 1, "SW",
            new[] { ('N', 0, -30f), ('N', 0, -14f), ('E', 0, -38f) }, 0,
            new[] { new Vector2(-42, -28), new Vector2(-4, -42) });
        Building("South_East", 4, -44, 44, -28, 1, "SE",
            new[] { ('N', 0, 14f), ('N', 0, 30f), ('W', 0, -38f) }, 1,
            new[] { new Vector2(42, -28), new Vector2(4, -42) });

        // ---- Мосты на втором ярусе ----
        root = new GameObject("Bridges").transform;
        root.SetParent(keepRoot, false);
        foreach (float z in new[] { 18f, 22f, 26f })
        {
            Put(pStoneFloor, -22f, y2, z, 0f); // донжон — казармы
            Put(pStoneFloor, 22f, y2, z, 0f);  // зал — оружейная
        }
        foreach (float z in new[] { 34f, 38f, 42f, -42f })
        {
            Put(pStoneFloor, -2f, y2, z, 0f);  // над улицей и над воротами
            Put(pStoneFloor, 2f, y2, z, 0f);
        }
        root = keepRoot;

        // ---- Станки ----
        Put(pWorkbench, 30f, y, 34f, 0f);
        Put(pStonecutter, 37f, y, 34f, 0f);
        Put(pWorkbench, 20f, y, -20f, 0f);     // во дворе у южных ворот

        // ---- Баррикады на улице и снаружи у пандуса ----
        foreach (float z in new[] { 38f, 20f, -20f, -38f })
        {
            Put(pBarricade, -2f, y, z, z > 0 ? 0f : 180f);
            Put(pBarricade, 2f, y, z, z > 0 ? 0f : 180f);
        }
        for (int i = -2; i <= 2; i++)
            Put(pBarricade, i * 5f, 0f, PlateauHalf + 22f - Mathf.Abs(i) * 1.5f, 0f);

        // ---- Декор ----
        root = new GameObject("Castle_Decor").transform;
        root.SetParent(keepRoot, false);
        Box("Campfire", new Vector3(-24f, y + 0.25f, -20f), new Vector3(1.4f, 0.5f, 1.4f), stone);
        MakeTorch(new Vector3(-24f, y - 1.6f, -20f));
        Box("Well", new Vector3(-26f, y + 0.5f, 22f), new Vector3(2.4f, 1f, 2.4f), stone);
        Box("Well_Water", new Vector3(-26f, y + 0.9f, 22f), new Vector3(1.8f, 0.1f, 1.8f), Mat("Water", new Color(0.05f, 0.08f, 0.12f)));
        for (int i = 0; i < 6; i++)
            Box("Crate", new Vector3(30f + (i % 3) * 1.1f, y + 0.5f + (i / 3) * 1f, -18f), Vector3.one, darkWood);
        for (int i = 0; i < 4; i++)
            Box("Crate", new Vector3(-40f + i * 1.1f, y + 0.5f, 31f), Vector3.one, darkWood);
        // Кристалл лайлилиума в донжоне
        GameObject cr = Box("Lailillium_Crystal", new Vector3(-18f, y + 1.4f, 10f), new Vector3(1f, 2.8f, 1f), crystal);
        cr.transform.rotation = Quaternion.Euler(0f, 45f, 8f);
        Box("Crystal_Base", new Vector3(-18f, y + 0.2f, 10f), new Vector3(3f, 0.4f, 3f), stone);
        GameObject lg = new GameObject("Crystal_Light");
        lg.transform.SetParent(root, false);
        lg.transform.position = new Vector3(-18f, y + 2.5f, 10f);
        Light l = lg.AddComponent<Light>();
        l.type = LightType.Point; l.color = new Color(0.65f, 0.4f, 1f); l.range = 12f; l.intensity = 4f;
        // Знамёна на улице у северных ворот
        foreach (float x in new[] { -3.3f, 3.3f })
            Box("Banner", new Vector3(x, y + 2.6f, 32f), new Vector3(0.05f, 2.4f, 1.2f), cloth);

        // Факелы: улица, дворы, внутри корпусов (горят всегда), снаружи у пандусов
        foreach (Vector3 t in new[]
        {
            new Vector3(-3f, y, 26f), new Vector3(3f, y, 10f), new Vector3(-3f, y, -6f), new Vector3(3f, y, -24f),
            new Vector3(-20f, y, 24f), new Vector3(20f, y, 24f), new Vector3(-10f, y, -20f), new Vector3(10f, y, -16f),
            new Vector3(-40f, y, 0f), new Vector3(40f, y, 0f), new Vector3(8f, y, 0f),
            new Vector3(-6f, 0f, PlateauHalf + 15f), new Vector3(6f, 0f, PlateauHalf + 15f),
            new Vector3(-6f, 0f, -PlateauHalf - 15f), new Vector3(6f, 0f, -PlateauHalf - 15f),
        })
            MakeTorch(t);
        foreach (Vector3 t in new[]
        {
            new Vector3(-26f, y, 36f), new Vector3(12f, y, 36f), new Vector3(-20f, y, -36f), new Vector3(20f, y, -36f),
            new Vector3(-16f, y, -8f), new Vector3(-24f, y2 + 0.25f, 10f), new Vector3(-24f, y2 + Floor + 0.25f, 10f),
            new Vector3(30f, y, 10f), new Vector3(26f, y2 + 0.25f, 10f),
        })
            MakeTorch(t, true);
    }

    // Корпус из каменных деталей.
    //   x0..x1, z0..z1 — углы (кратно 4), levels — сколько этажей, крыша сверху всегда.
    //   skip — каких стен нет (их заменяет внешняя стена замка): буквы N S W E.
    //   doors — (сторона, этаж, где по стене) — там арка-проход.
    //   stairCol — лестницы у западной (0) или восточной (1) стены; на следующем этаже — у другой.
    //   roofGaps — где на крыше не ставить зубцы (там мост или галерея).
    static void Building(string name, float x0, float z0, float x1, float z1, int levels, string skip,
                         (char side, int level, float at)[] doors, int stairCol, Vector2[] roofGaps)
    {
        Transform keep = root;
        root = new GameObject(name).transform;
        root.SetParent(keep, false);
        int cols = Mathf.RoundToInt((x1 - x0) / 4f), rows = Mathf.RoundToInt((z1 - z0) / 4f);

        bool IsDoor(char side, int level, float at)
        {
            foreach (var d in doors) if (d.side == side && d.level == level && Mathf.Abs(d.at - at) < 0.1f) return true;
            return false;
        }
        bool IsGap(float x, float z)
        {
            foreach (Vector2 g in roofGaps) if (Mathf.Abs(g.x - x) < 0.1f && Mathf.Abs(g.y - z) < 0.1f) return true;
            return false;
        }

        for (int l = 0; l <= levels; l++)
        {
            float y = Top + Floor * l;
            bool roofLevel = l == levels;
            // Стены этажа (или зубцы крыши)
            for (int k = 0; k < cols; k++)
            {
                float x = x0 + 2f + 4f * k;
                if (skip.IndexOf('N') < 0) WallPiece('N', l, x, z1, 0f, roofLevel, k, IsDoor('N', l, x), IsGap(x, z1));
                if (skip.IndexOf('S') < 0) WallPiece('S', l, x, z0, 180f, roofLevel, k, IsDoor('S', l, x), IsGap(x, z0));
            }
            for (int k = 0; k < rows; k++)
            {
                float z = z0 + 2f + 4f * k;
                if (skip.IndexOf('W') < 0) WallPiece('W', l, x0, z, 90f, roofLevel, k, IsDoor('W', l, z), IsGap(x0, z));
                if (skip.IndexOf('E') < 0) WallPiece('E', l, x1, z, 90f, roofLevel, k, IsDoor('E', l, z), IsGap(x1, z));
            }
            // Колонны на углах (где обе стены свои)
            if (!roofLevel)
                foreach (float cx in new[] { x0, x1 })
                    foreach (float cz in new[] { z0, z1 })
                    {
                        bool own = skip.IndexOf(cx == x0 ? 'W' : 'E') < 0 && skip.IndexOf(cz == z0 ? 'S' : 'N') < 0;
                        if (own) Put(pStoneColumn, cx, y, cz, 0f);
                    }
            if (roofLevel) break;

            // Лестница с этого этажа на следующий + дыра в потолке над ней
            int col = (l % 2 == 0) == (stairCol == 0) ? 0 : cols - 1;
            float sx = col == 0 ? x0 + 1.5f : x1 - 1.5f;
            Put(pStoneStairs, sx, l == 0 ? Top : y + 0.25f, z0 + 8f, 0f);

            // Перекрытие (пол следующего этажа / крыша)
            float fy = y + Floor;
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                {
                    if (i == col && (j == 1 || j == 2)) continue; // дыра для лестницы
                    Put(pStoneFloor, x0 + 2f + 4f * i, fy, z0 + 2f + 4f * j, 0f);
                }
        }
        root = keep;
    }

    static void WallPiece(char side, int level, float x, float z, float yaw, bool roof, int k, bool door, bool gap)
    {
        float y = Top + Floor * level;
        if (roof)
        {
            if (!gap) Put(pStoneParapet, x, y, z, yaw);
            return;
        }
        BuildPiece p = door ? pStoneArch : (level > 0 || k % 3 == 1) && k % 2 == 1 ? pStoneSlit : pStoneWallPlain;
        Put(p, x, y, z, yaw);
    }

    // Факел: столб + огонёк + свет (горит ночью, в подземелье — всегда)
    static void MakeTorch(Vector3 pos, bool always = false)
    {
        GameObject t = new GameObject("Torch");
        t.transform.SetParent(root, false);
        t.transform.position = pos;
        Part(t.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(0.15f, 2f, 0.15f), darkWood, Vector3.zero, false);
        GameObject flame = Part(t.transform, PrimitiveType.Cube, new Vector3(0f, 2.1f, 0f), new Vector3(0.22f, 0.3f, 0.22f), torchFlame, new Vector3(0f, 45f, 0f), false);
        flame.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        GameObject lg = new GameObject("Light");
        lg.transform.SetParent(t.transform, false);
        lg.transform.localPosition = new Vector3(0f, 2.3f, 0f);
        Light l = lg.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.62f, 0.3f);
        l.range = always ? 9f : 11f;
        l.intensity = 2.5f;
        l.shadows = LightShadows.None;
        Torch torch = t.AddComponent<Torch>();
        torch.torchLight = l;
        torch.flame = flame.GetComponent<Renderer>();
        torch.always = always;
    }

    // ================= Деревня (юг, за фортом) =================
    static readonly Vector3[] VillageSpots =
    {
        new Vector3(-22, -88, 0), new Vector3(-8, -90, 90), new Vector3(8, -87, 0), new Vector3(22, -92, 90),
        new Vector3(-24, -106, 90), new Vector3(-10, -110, 0), new Vector3(6, -106, 0), new Vector3(20, -112, 90),
        new Vector3(-4, -126, 0), new Vector3(12, -128, 0),
    };

    static void BuildVillage()
    {
        root = new GameObject("Village").transform;
        markVillage = true;
        foreach (Vector3 s in VillageSpots) BuildHouse(s.x, 0f, s.y, s.z);
        markVillage = false;

        foreach (Vector3 s in VillageSpots) MakeTorch(new Vector3(s.x + 3f, 0f, s.y - 4f));

        root = new GameObject("Village_Decor").transform;
        GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = "Airship_LandingPad";
        pad.transform.SetParent(root, false);
        pad.transform.position = new Vector3(55f, 0.1f, -80f);
        pad.transform.localScale = new Vector3(16f, 0.1f, 16f);
        pad.GetComponent<Renderer>().sharedMaterial = darkWood;
        pad.isStatic = true;
    }

    // ================= Где нельзя ставить деревья и камни =================
    static bool Blocked(Vector3 p)
    {
        if (Mathf.Abs(p.x) < 62f && Mathf.Abs(p.z) < 70f) return true;                 // плато, пандусы
        if (p.x > -40f && p.x < 40f && p.z > -142f && p.z < -74f) return true;         // деревня
        if (p.x > 36f && p.x < 74f && p.z > -100f && p.z < -60f) return true;          // площадка дирижабля
        if (Mathf.Abs(p.x) < 9f && p.z > -80f && p.z < AbyssEdge) return true;         // дорога тварей
        if (p.z > AbyssEdge - 12f) return true;                                        // край пропасти
        return false;
    }

    static void BuildForest()
    {
        root = new GameObject("Forest").transform;
        Random.State saved = Random.state;
        Random.InitState(1234);
        int placed = 0, tries = 0;
        while (placed < 320 && tries < 8000)
        {
            tries++;
            // Больше деревьев на севере, между фортом и пропастью
            float z = Random.value < 0.55f ? Random.Range(70f, AbyssEdge - 12f) : Random.Range(-290f, 70f);
            Vector3 p = new Vector3(Random.Range(-290f, 290f), 0f, z);
            if (Blocked(p)) continue;
            float s = Random.Range(0.8f, 1.6f);
            bool dead = p.z > 110f && Random.value < 0.5f; // ближе к Расколу деревья мёртвые

            GameObject tree = new GameObject(dead ? "DeadTree" : "Tree");
            tree.transform.SetParent(root, false);
            tree.transform.position = p;
            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0f, 2f * s, 0f);
            trunk.transform.localScale = new Vector3(0.5f * s, 2f * s, 0.5f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = darkWood;
            if (dead)
            {
                for (int k = 0; k < 3; k++)
                    Part(tree.transform, PrimitiveType.Cube, new Vector3(0f, (2.5f + k * 0.8f) * s, 0f), new Vector3(0.12f, 0.12f, 1.8f * s), darkWood,
                         new Vector3(-30f + k * 10f, k * 120f, 0f), false);
            }
            else
            {
                GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                crown.name = "Crown";
                crown.transform.SetParent(tree.transform, false);
                crown.transform.localPosition = new Vector3(0f, 5f * s, 0f);
                crown.transform.localScale = Vector3.one * 3.5f * s;
                crown.GetComponent<Renderer>().sharedMaterial = leaves;
                Object.DestroyImmediate(crown.GetComponent<Collider>());
            }

            ResourceNode node = tree.AddComponent<ResourceNode>();
            node.netId = ++nodeIdCounter;
            node.displayName = dead ? "Мёртвое дерево" : "Дерево";
            node.resource = ResourceType.Wood;
            node.requiredTool = Tool.Axe;
            node.maxHits = 3 + Mathf.RoundToInt(s * 2f);
            node.yieldPerHit = dead ? 1 : 2;
            node.bonusOnBreak = (dead ? 2 : 4) + Mathf.RoundToInt(s * 3f);
            placed++;
        }
        Random.state = saved;
    }

    // ================= Камни (добываются киркой) =================
    static void BuildRocks()
    {
        root = new GameObject("Rocks").transform;
        Random.State saved = Random.state;
        Random.InitState(777);
        int placed = 0, tries = 0;
        while (placed < 160 && tries < 8000)
        {
            tries++;
            Vector3 p = new Vector3(Random.Range(-290f, 290f), 0f, Random.Range(-290f, AbyssEdge - 12f));
            if (Blocked(p)) continue;
            Vector3 size = new Vector3(Random.Range(1.2f, 2.8f), Random.Range(0.8f, 1.7f), Random.Range(1.2f, 2.8f));

            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rock.name = "Rock";
            rock.transform.SetParent(root, false);
            rock.transform.position = p + Vector3.up * size.y * 0.3f;
            rock.transform.localScale = size;
            rock.transform.rotation = Quaternion.Euler(Random.Range(-10f, 10f), Random.Range(0f, 360f), Random.Range(-10f, 10f));
            rock.GetComponent<Renderer>().sharedMaterial = rockMat;

            ResourceNode node = rock.AddComponent<ResourceNode>();
            node.netId = ++nodeIdCounter;
            node.displayName = "Камень";
            node.resource = ResourceType.Stone;
            node.requiredTool = Tool.Pickaxe;
            node.maxHits = 3 + Mathf.RoundToInt(size.x + size.z) / 2;
            node.yieldPerHit = 2;
            node.bonusOnBreak = 5;
            placed++;
        }
        Random.state = saved;
    }

    // ================= Кристаллы лайлилиума: у края пропасти и в тупиках подземелья =================
    static void BuildLailillium(bool[,] open)
    {
        root = new GameObject("Lailillium").transform;
        Random.State saved = Random.state;
        Random.InitState(4242);
        int placed = 0, tries = 0;
        while (placed < 12 && tries < 3000)
        {
            tries++;
            Vector3 p = new Vector3(Random.Range(-260f, 260f), 0f, Random.Range(AbyssEdge - 30f, AbyssEdge - 4f));
            if (Mathf.Abs(p.x) < 12f) continue;
            MakeCrystal(p, 1f);
            placed++;
        }
        Random.state = saved;

        // Тупики лабиринта: комната с одним выходом
        int found = 0;
        for (int i = 1; i < Cells - 1 && found < 6; i += 2)
            for (int j = 1; j < Cells - 1 && found < 6; j += 2)
            {
                if (!open[i, j]) continue;
                int exits = (open[i + 1, j] ? 1 : 0) + (open[i - 1, j] ? 1 : 0) + (open[i, j + 1] ? 1 : 0) + (open[i, j - 1] ? 1 : 0);
                if (exits != 1) continue;
                bool stair = false;
                foreach (var s in Stairwells) if ((s.i == i && s.j == j) || (s.wi == i && s.wj == j)) stair = true;
                if (stair) continue;
                MakeCrystal(new Vector3(CellX(i), 0.05f, CellX(j)), 0.8f);
                found++;
            }
    }

    static void MakeCrystal(Vector3 p, float s)
    {
        GameObject c = new GameObject("Lailillium_Node");
        c.transform.SetParent(root, false);
        c.transform.position = p;
        Part(c.transform, PrimitiveType.Cube, new Vector3(0f, 0.9f * s, 0f), new Vector3(0.6f, 1.8f, 0.6f) * s, crystal, new Vector3(5f, 30f, 10f));
        Part(c.transform, PrimitiveType.Cube, new Vector3(0.5f * s, 0.5f * s, 0.2f * s), new Vector3(0.4f, 1.1f, 0.4f) * s, crystal, new Vector3(-20f, 10f, -25f));
        Part(c.transform, PrimitiveType.Cube, new Vector3(-0.45f * s, 0.4f * s, -0.2f * s), new Vector3(0.35f, 0.9f, 0.35f) * s, crystal, new Vector3(15f, 60f, 30f));
        GameObject lg = new GameObject("Glow");
        lg.transform.SetParent(c.transform, false);
        lg.transform.localPosition = new Vector3(0f, 1.2f, 0f);
        Light l = lg.AddComponent<Light>();
        l.type = LightType.Point; l.color = new Color(0.65f, 0.4f, 1f); l.range = 7f; l.intensity = 2f; l.shadows = LightShadows.None;

        ResourceNode node = c.AddComponent<ResourceNode>();
        node.netId = ++nodeIdCounter;
        node.displayName = "Кристалл лайлилиума";
        node.resource = ResourceType.Lailillium;
        node.requiredTool = Tool.Pickaxe;
        node.maxHits = 5;
        node.yieldPerHit = 1;
        node.bonusOnBreak = 2;
    }

    // ================= Раскол: пропасть, туман, вторая половина, ядро =================
    static void BuildAbyss()
    {
        root = new GameObject("Abyss").transform;

        // Обрыв — стена скалы вниз от края земли
        Box("Cliff", new Vector3(0f, -200.05f, AbyssEdge - 5f), new Vector3(600f, 400f, 10f), abyssRock);
        // Выступы скалы глубже (выше их твари не мешают — они лезут с -15 м)
        Random.State saved = Random.state;
        Random.InitState(99);
        for (int i = 0; i < 40; i++)
        {
            float x = Random.Range(-290f, 290f);
            GameObject r = Box("Cliff_Rock", new Vector3(x, Random.Range(-160f, -28f), AbyssEdge + Random.Range(0f, 3f)),
                               new Vector3(Random.Range(6f, 22f), Random.Range(4f, 14f), Random.Range(3f, 8f)), abyssRock);
            r.transform.rotation = Quaternion.Euler(Random.Range(-20f, 20f), Random.Range(-30f, 30f), Random.Range(-30f, 30f));
            Object.DestroyImmediate(r.GetComponent<Collider>());
        }
        // Рваный край (только по бокам, чтобы не мешать тварям в середине)
        for (int i = 0; i < 50; i++)
        {
            float x = Random.Range(90f, 295f) * (Random.value < 0.5f ? -1f : 1f);
            GameObject r = Box("Edge_Rock", new Vector3(x, Random.Range(0.2f, 1.2f), AbyssEdge - Random.Range(1f, 6f)),
                               new Vector3(Random.Range(2f, 6f), Random.Range(1f, 4f), Random.Range(2f, 5f)), abyssRock);
            r.transform.rotation = Quaternion.Euler(Random.Range(-15f, 15f), Random.Range(0f, 360f), Random.Range(-15f, 15f));
        }

        // Ядро планеты — далеко внизу, светится синим
        Material core = Mat("Core", new Color(0.3f, 0.6f, 1f), new Color(0.25f, 0.6f, 1f) * 12f);
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Planet_Core";
        sphere.transform.SetParent(root, false);
        sphere.transform.position = new Vector3(0f, -650f, 520f);
        sphere.transform.localScale = Vector3.one * 700f;
        sphere.GetComponent<Renderer>().sharedMaterial = core;
        sphere.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Object.DestroyImmediate(sphere.GetComponent<Collider>());
        GameObject coreLight = new GameObject("Core_Light");
        coreLight.transform.SetParent(root, false);
        coreLight.transform.position = new Vector3(0f, -220f, 330f);
        Light cl = coreLight.AddComponent<Light>();
        cl.type = LightType.Point; cl.color = new Color(0.3f, 0.55f, 1f); cl.range = 420f; cl.intensity = 6f; cl.shadows = LightShadows.None;

        // Вторая половина мира — тёмная земля далеко за пропастью, с горами
        Material farMat = Mat("FarLand", new Color(0.05f, 0.04f, 0.07f), new Color(0.14f, 0.03f, 0.2f));
        GameObject far = Box("Dark_Half", new Vector3(0f, -160f, 1500f), new Vector3(3200f, 500f, 520f), farMat);
        far.transform.rotation = Quaternion.Euler(-4f, 0f, 2f);
        Object.DestroyImmediate(far.GetComponent<Collider>());
        for (int i = 0; i < 30; i++)
        {
            float h = Random.Range(60f, 260f);
            GameObject m = Box("Dark_Mountain", new Vector3(Random.Range(-1500f, 1500f), 90f + h / 2f - 20f, Random.Range(1300f, 1700f)),
                               new Vector3(Random.Range(60f, 200f), h, Random.Range(60f, 200f)), farMat);
            m.transform.rotation = Quaternion.Euler(Random.Range(-8f, 8f), Random.Range(0f, 90f), Random.Range(-8f, 8f));
            Object.DestroyImmediate(m.GetComponent<Collider>());
        }
        // Светящиеся трещины на обрыве той стороны
        Material crack = Mat("DarkCrack", new Color(0.5f, 0.15f, 0.7f), new Color(0.6f, 0.15f, 0.9f) * 6f);
        for (int i = 0; i < 12; i++)
        {
            GameObject c = Box("Dark_Crack", new Vector3(Random.Range(-1200f, 1200f), Random.Range(-300f, 60f), 1238f),
                               new Vector3(Random.Range(3f, 8f), Random.Range(60f, 200f), 2f), crack);
            c.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(-35f, 35f));
            Object.DestroyImmediate(c.GetComponent<Collider>());
        }

        // Обломки, парящие в пустоте
        for (int i = 0; i < 45; i++)
        {
            GameObject r = Box("Floating_Rock", new Vector3(Random.Range(-600f, 600f), Random.Range(-350f, 40f), Random.Range(230f, 1200f)),
                               Vector3.one * Random.Range(4f, 38f), abyssRock);
            r.transform.localScale = Vector3.Scale(r.transform.localScale, new Vector3(1f, Random.Range(0.4f, 0.9f), Random.Range(0.6f, 1.2f)));
            r.transform.rotation = Random.rotation;
            Object.DestroyImmediate(r.GetComponent<Collider>());
        }
        Random.state = saved;

        // Туман Раскола — слои полупрозрачной мглы под краем, из неё лезут твари
        Material mist = MistMat();
        foreach (float my in new[] { -5f, -14f, -30f, -60f, -110f })
        {
            GameObject q = GameObject.CreatePrimitive(PrimitiveType.Plane);
            q.name = "Abyss_Mist";
            q.transform.SetParent(root, false);
            q.transform.position = new Vector3(0f, my, AbyssEdge + 230f);
            q.transform.localScale = new Vector3(70f, 1f, 46f);
            q.GetComponent<Renderer>().sharedMaterial = mist;
            q.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Object.DestroyImmediate(q.GetComponent<Collider>());
        }
        // Фиолетовое свечение снизу у края
        for (int i = -3; i <= 3; i++)
        {
            GameObject lg = new GameObject("Abyss_Glow");
            lg.transform.SetParent(root, false);
            lg.transform.position = new Vector3(i * 80f, -10f, AbyssEdge + 8f);
            Light l = lg.AddComponent<Light>();
            l.type = LightType.Point; l.color = new Color(0.5f, 0.2f, 0.8f); l.range = 45f; l.intensity = 3f; l.shadows = LightShadows.None;
        }
    }

    // Невидимые края карты (на севере края нет — там пропасть, можно сорваться)
    static void BuildBorders()
    {
        root = new GameObject("MapBorders").transform;
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0f, 20f, -300f), new Vector3(600f, 60f, 1f)),
            (new Vector3(300f, 20f, -60f), new Vector3(1f, 60f, 480f)),
            (new Vector3(-300f, 20f, -60f), new Vector3(1f, 60f, 480f)),
        })
        {
            GameObject b = Box("MapBorder", pos, size, dark);
            Object.DestroyImmediate(b.GetComponent<Renderer>());
            Object.DestroyImmediate(b.GetComponent<MeshFilter>());
        }
    }

    // ================= Камера (до входа в игру смотрит на форт и Раскол) =================
    static void SetupCamera()
    {
        Camera cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
        }
        cam.transform.SetPositionAndRotation(new Vector3(60f, 45f, -110f), Quaternion.Euler(18f, -22f, 0f));
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 3500f; // чтобы было видно вторую половину мира
        EnableAntiAliasing(cam);
        cam.gameObject.AddComponent<FpsCounter>();
    }

    // ================= Сетевой игрок (префаб) =================
    static GameObject BuildPlayerPrefab()
    {
        GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.GetComponent<Renderer>().sharedMaterial = playerMat;
        Object.DestroyImmediate(player.GetComponent<CapsuleCollider>());
        player.AddComponent<CharacterController>();

        player.AddComponent<NetworkObject>();
        OwnerNetworkTransform nt = player.AddComponent<OwnerNetworkTransform>();
        nt.SyncRotAngleX = false; // по сети передаём только поворот влево-вправо
        nt.SyncRotAngleZ = false;
        nt.SyncScaleX = false; nt.SyncScaleY = false; nt.SyncScaleZ = false;

        PlayerMovement movement = player.AddComponent<PlayerMovement>();

        GameObject pivot = new GameObject("CameraPivot");
        pivot.transform.SetParent(player.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 0.6f, 0f);
        PlayerCamera pcam = pivot.AddComponent<PlayerCamera>();
        pcam.playerBody = player.transform;

        // Инструменты (пока из кубиков) — в правой руке
        GameObject sword = new GameObject("Sword");
        sword.transform.SetParent(pivot.transform, false);
        sword.transform.localPosition = new Vector3(0.45f, -0.35f, 0.55f);
        sword.transform.localRotation = Quaternion.Euler(60f, 0f, 0f);
        // клинок длинный (1.4 м): бьёт ровно то, что видно на экране
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, 0.78f, 0f), new Vector3(0.07f, 1.4f, 0.02f), steel, Vector3.zero, false);
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, 1.5f, 0f), new Vector3(0.045f, 0.06f, 0.018f), steel, new Vector3(0f, 0f, 45f), false); // остриё
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0f), new Vector3(0.25f, 0.04f, 0.05f), darkWood, Vector3.zero, false);
        Part(sword.transform, PrimitiveType.Cube, new Vector3(0f, -0.08f, 0f), new Vector3(0.04f, 0.2f, 0.04f), darkWood, Vector3.zero, false);

        GameObject hammer = new GameObject("Hammer");
        hammer.transform.SetParent(pivot.transform, false);
        hammer.transform.localPosition = new Vector3(0.45f, -0.4f, 0.55f);
        hammer.transform.localRotation = Quaternion.Euler(50f, 0f, 0f);
        Part(hammer.transform, PrimitiveType.Cube, new Vector3(0f, 0.2f, 0f), new Vector3(0.05f, 0.55f, 0.05f), darkWood, Vector3.zero, false);
        Part(hammer.transform, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), new Vector3(0.22f, 0.12f, 0.12f), steel, Vector3.zero, false);
        hammer.SetActive(false);

        GameObject axe = new GameObject("Axe");
        axe.transform.SetParent(pivot.transform, false);
        axe.transform.localPosition = new Vector3(0.45f, -0.4f, 0.55f);
        axe.transform.localRotation = Quaternion.Euler(50f, 0f, 0f);
        Part(axe.transform, PrimitiveType.Cube, new Vector3(0f, 0.22f, 0f), new Vector3(0.05f, 0.6f, 0.05f), darkWood, Vector3.zero, false);
        Part(axe.transform, PrimitiveType.Cube, new Vector3(0.08f, 0.47f, 0f), new Vector3(0.16f, 0.14f, 0.03f), steel, Vector3.zero, false);
        axe.SetActive(false);

        GameObject pick = new GameObject("Pickaxe");
        pick.transform.SetParent(pivot.transform, false);
        pick.transform.localPosition = new Vector3(0.45f, -0.4f, 0.55f);
        pick.transform.localRotation = Quaternion.Euler(50f, 0f, 0f);
        Part(pick.transform, PrimitiveType.Cube, new Vector3(0f, 0.22f, 0f), new Vector3(0.05f, 0.6f, 0.05f), darkWood, Vector3.zero, false);
        Part(pick.transform, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), new Vector3(0.42f, 0.05f, 0.05f), steel, new Vector3(0f, 0f, 8f), false);
        pick.SetActive(false);

        // Лук: рукоять и два плеча (тетиву и стрелу добавляет PlayerBow)
        GameObject bow = new GameObject("Bow");
        bow.transform.SetParent(pivot.transform, false);
        bow.transform.localPosition = new Vector3(0.32f, -0.28f, 0.6f);
        bow.transform.localRotation = Quaternion.Euler(0f, 0f, 12f);
        Part(bow.transform, PrimitiveType.Cube, Vector3.zero, new Vector3(0.05f, 0.22f, 0.06f), darkWood, Vector3.zero, false);
        Part(bow.transform, PrimitiveType.Cube, new Vector3(0f, 0.32f, -0.05f), new Vector3(0.035f, 0.46f, 0.035f), darkWood, new Vector3(18f, 0f, 0f), false);
        Part(bow.transform, PrimitiveType.Cube, new Vector3(0f, -0.32f, -0.05f), new Vector3(0.035f, 0.46f, 0.035f), darkWood, new Vector3(-18f, 0f, 0f), false);
        bow.SetActive(false);

        foreach (GameObject tool in new[] { sword, hammer, axe, pick, bow })
            foreach (Renderer r in tool.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        PlayerHotbar hotbar = player.AddComponent<PlayerHotbar>();
        hotbar.swordModel = sword.transform;
        hotbar.hammerModel = hammer.transform;
        hotbar.axeModel = axe.transform;
        hotbar.pickaxeModel = pick.transform;
        hotbar.bowModel = bow.transform;

        Inventory inventory = player.AddComponent<Inventory>();
        PlayerCombat combat = player.AddComponent<PlayerCombat>();
        PlayerBow bowCtl = player.AddComponent<PlayerBow>();
        PlayerBuilder builder = player.AddComponent<PlayerBuilder>();
        builder.ghostMaterial = ghost;
        builder.pieces = catalog.FindAll(p => p.buildable);
        PlayerInteract interact = player.AddComponent<PlayerInteract>();
        PlayerHealth health = player.AddComponent<PlayerHealth>();

        // Управление работает только у своего игрока — включит NetworkPlayer
        Behaviour[] ownerOnly = { movement, pcam, hotbar, inventory, combat, bowCtl, builder, interact, health };
        foreach (Behaviour b in ownerOnly) b.enabled = false;

        NetworkPlayer np = player.AddComponent<NetworkPlayer>();
        np.ownerOnly = ownerOnly;
        np.cameraPivot = pivot.transform;
        np.bodyRenderer = player.GetComponent<Renderer>();
        np.spawnPoint = new Vector3(8f, Top + 1.2f, -20f); // в южном дворе замка

        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(player, "Assets/Prefabs/Player.prefab");
        Object.DestroyImmediate(player);
        return prefab;
    }

    // ================= Сеть =================
    static void SetupNetwork(GameObject playerPrefab)
    {
        GameObject netGo = new GameObject("NetworkManager");
        NetworkManager nm = netGo.AddComponent<NetworkManager>();
        UnityTransport transport = netGo.AddComponent<UnityTransport>();
        if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
        nm.NetworkConfig.NetworkTransport = transport;
        nm.NetworkConfig.PlayerPrefab = playerPrefab;
        nm.NetworkConfig.EnableSceneManagement = false; // у всех одна и та же сцена форта
        netGo.AddComponent<NetworkMenu>();

        // Менеджеры — обычные объекты (без NetworkObject): по сети они общаются сообщениями
        GameObject manager = new GameObject("BuildingManager");
        BuildingManager bm = manager.AddComponent<BuildingManager>();
        bm.catalog = new List<BuildPiece>(catalog);

        // Ход игры: день/ночь, деревня, поражение
        GameObject dirGo = new GameObject("GameDirector");
        GameDirector gd = dirGo.AddComponent<GameDirector>();
        Light sunLight = null;
        foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sunLight = l; break; }
        gd.sun = sunLight;
        GameObject moonGo = new GameObject("Moon");
        moonGo.transform.rotation = Quaternion.Euler(50f, 140f, 0f);
        Light moon = moonGo.AddComponent<Light>();
        moon.type = LightType.Directional;
        moon.color = new Color(0.55f, 0.65f, 1f);
        moon.intensity = 0f;
        moon.shadows = LightShadows.None;
        gd.moon = moon;

        GameObject enemiesGo = new GameObject("EnemyManager");
        EnemyManager em = enemiesGo.AddComponent<EnemyManager>();
        em.bodyMaterial = Mat("EnemyBody", new Color(0.16f, 0.1f, 0.2f));
        em.eyeMaterial = Mat("EnemyEyes", new Color(1f, 0.2f, 0.1f), new Color(1f, 0.15f, 0.05f) * 4f);
        em.spawnCenter = new Vector3(0f, -9f, AbyssEdge + 1f);
        em.villageCenter = new Vector3(0f, 0f, -105f);
        em.path = new[]
        {
            new Vector3(0f, 0f, PlateauHalf + 27f), new Vector3(0f, 0f, PlateauHalf + 14f), new Vector3(0f, Top, WallLine),
            new Vector3(0f, Top, 0f), new Vector3(0f, Top, -WallLine), new Vector3(0f, 0f, -PlateauHalf - 16f),
        };
    }

    // Чтобы можно было запустить две копии игры на одном компе и проверить сеть
    static void SetupPlayerSettings()
    {
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
    }

    // ================= Атмосфера: тёмная, холодная, с дымкой =================
    static void SetupAtmosphere()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = new Color(0.2f, 0.21f, 0.26f);
        RenderSettings.fogDensity = 0.0011f;

        // Небо — темнее и холоднее обычного
        Shader skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            string path = "Assets/Materials/DarkSky.mat";
            Material sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
                sky = new Material(skyShader);
                AssetDatabase.CreateAsset(sky, path);
            }
            if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.32f, 0.3f, 0.38f));
            if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.05f, 0.05f, 0.08f));
            if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 0.75f);
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
        }

        Light sun = Object.FindFirstObjectByType<Light>();
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            sun.color = new Color(0.95f, 0.86f, 0.76f);
            sun.intensity = 0.9f;
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

    static void MakeTransparent(Material m)
    {
        if (!m.HasProperty("_Surface")) return;
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
        EditorUtility.SetDirty(m);
    }

    // Полупрозрачный материал для «призрака» в режиме стройки
    static Material GhostMat()
    {
        Material m = Mat("Ghost", new Color(0.3f, 1f, 0.4f, 0.4f));
        MakeTransparent(m);
        return m;
    }

    // Мгла Раскола: тёмно-фиолетовая, полупрозрачная, чуть светится
    static Material MistMat()
    {
        Material m = Mat("AbyssMist", new Color(0.16f, 0.07f, 0.24f, 0.32f), new Color(0.12f, 0.03f, 0.2f));
        MakeTransparent(m);
        return m;
    }
}
