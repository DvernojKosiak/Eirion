using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Скрипт ДЛЯ РЕДАКТОРА (не для игры). Лежит в папке Assets/Editor.
// Добавляет в верхнее меню Unity пункт "Eirion -> Создать тестовую сцену".
// Он сам строит сцену: землю, препятствия, героя и камеру — и сохраняет её.
public static class EirionSceneBuilder
{
    const string ScenePath = "Assets/Scenes/TestScene.unity";

    [MenuItem("Eirion/Создать тестовую сцену")]
    public static void CreateTestScene()
    {
        // Если в текущей сцене есть несохранённые изменения — спросим, сохранить ли
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
            !EditorUtility.DisplayDialog("Eirion", "Тестовая сцена уже есть. Пересоздать её заново?", "Да", "Отмена"))
            return;

        // Новая сцена со светом и камерой по умолчанию
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        Material groundMat = GetOrCreateMaterial("Ground", new Color(0.35f, 0.4f, 0.3f));
        Material stoneMat  = GetOrCreateMaterial("Stone",  new Color(0.5f, 0.5f, 0.52f));
        Material playerMat = GetOrCreateMaterial("Player", new Color(0.7f, 0.25f, 0.2f));

        // --- Земля 100 x 100 метров ---
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(10f, 1f, 10f);
        ground.GetComponent<Renderer>().sharedMaterial = groundMat;

        // --- Препятствия: кубы, стена, наклонная плита ---
        GameObject obstacles = new GameObject("Obstacles");
        MakeBox("Cube_1", new Vector3(4f, 0.5f, 6f),   new Vector3(1f, 1f, 1f),   Vector3.zero,              stoneMat, obstacles.transform);
        MakeBox("Cube_2", new Vector3(-5f, 1f, 8f),    new Vector3(2f, 2f, 2f),   Vector3.zero,              stoneMat, obstacles.transform);
        MakeBox("Wall",   new Vector3(0f, 2f, 15f),    new Vector3(12f, 4f, 1f),  Vector3.zero,              stoneMat, obstacles.transform);
        MakeBox("Ramp",   new Vector3(8f, 0.9f, 10f),  new Vector3(3f, 0.3f, 6f), new Vector3(-17f, 0f, 0f), stoneMat, obstacles.transform);

        // --- Герой ---
        GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.transform.position = new Vector3(0f, 1f, 0f);
        player.GetComponent<Renderer>().sharedMaterial = playerMat;
        Object.DestroyImmediate(player.GetComponent<CapsuleCollider>()); // у героя будет свой коллайдер
        player.AddComponent<CharacterController>();
        player.AddComponent<PlayerMovement>();

        // --- "Голова" и камера ---
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

        pivot.AddComponent<PlayerCamera>();

        // --- Сохраняем сцену и делаем её первой в списке сцен игры ---
        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path != ScenePath) scenes.Add(s);
        EditorBuildSettings.scenes = scenes.ToArray();

        Selection.activeGameObject = player;
        EditorUtility.DisplayDialog("Eirion", "Готово! Сцена TestScene создана и сохранена.\nНажми ▶ Play сверху.", "Ок");
    }

    static void MakeBox(string name, Vector3 pos, Vector3 scale, Vector3 euler, Material mat, Transform parent)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.position = pos;
        box.transform.localScale = scale;
        box.transform.rotation = Quaternion.Euler(euler);
        box.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // Материал (цвет поверхности) сохраняем файлом в Assets/Materials
    static Material GetOrCreateMaterial(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = "Assets/Materials/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        mat = new Material(shader);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
