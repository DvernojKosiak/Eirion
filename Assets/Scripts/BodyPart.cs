using UnityEngine;

// Части тела твари. По каждой можно ударить отдельно, и от этого разный результат.
public enum BodyPartType { Torso, Head, ArmL, ArmR, LegL, LegR, Sack }

// Одна часть тела (висит на «суставе»: плечо, бедро, шея...).
// Внутри — фигуры с коллайдером-триггером: клинок проходит сквозь них и понимает, куда попал.
public class BodyPart : MonoBehaviour
{
    public Enemy owner;
    public BodyPartType type;
    public Quaternion baseRotation;
    public Transform bend;                            // колено или локоть (у рук и ног), null — не гнётся

    [System.NonSerialized] public Vector3 react;     // насколько сустав сейчас отклонён ударом (градусы, ось — в теле твари)
    [System.NonSerialized] public Vector3 reactVel;
    [System.NonSerialized] public bool lost;         // отрублена
    // «болтанка»: конец руки или ноги — как грузик на верёвке (тянет вниз, отстаёт, когда тело дёргается)
    [System.NonSerialized] public Vector3 tip, tipVel;
    [System.NonSerialized] public bool tipInit;
    [System.NonSerialized] public float limp;        // 0 — двигается сам (анимация), 1 — висит и болтается

    public static bool IsArm(BodyPartType t) => t == BodyPartType.ArmL || t == BodyPartType.ArmR;
    public static bool IsLeg(BodyPartType t) => t == BodyPartType.LegL || t == BodyPartType.LegR;

    public static string Name(BodyPartType t)
    {
        switch (t)
        {
            case BodyPartType.Head: return "ГОЛОВА";
            case BodyPartType.ArmL: case BodyPartType.ArmR: return "РУКА";
            case BodyPartType.LegL: case BodyPartType.LegR: return "НОГА";
            case BodyPartType.Sack: return "МЕШОК";
            default: return "ТЕЛО";
        }
    }
}

// Собирает тело твари из простых фигур: ноги, туловище (сгорблено), голова, руки-когти.
public static class EnemyBody
{
    public static BodyPart[] Build(Transform vis, EnemyKind kind, Material body, Material eye, Material glow)
    {
        var parts = new BodyPart[7];
        bool brute = kind == EnemyKind.Brute;

        // Ноги (сустав — бедро на высоте 0.9)
        for (int s = -1; s <= 1; s += 2)
        {
            BodyPart leg = Pivot(vis, s < 0 ? BodyPartType.LegL : BodyPartType.LegR, new Vector3(0.22f * s, 0.9f, 0f), Vector3.zero);
            Prim(leg.transform, PrimitiveType.Cube, new Vector3(0f, -0.225f, 0f), new Vector3(0.2f, 0.48f, 0.22f), Vector3.zero, body, true);   // бедро
            Transform knee = Bend(leg.transform, "Knee", new Vector3(0f, -0.45f, 0f));
            Prim(knee, PrimitiveType.Cube, new Vector3(0f, -0.21f, 0f), new Vector3(0.18f, 0.46f, 0.2f), Vector3.zero, body, true);              // голень
            Prim(knee, PrimitiveType.Cube, new Vector3(0f, -0.42f, 0.1f), new Vector3(0.22f, 0.07f, 0.36f), Vector3.zero, body, false);          // ступня
            leg.bend = knee;
            parts[(int)leg.type] = leg;
        }

        // Туловище — сгорблено вперёд
        BodyPart torso = Pivot(vis, BodyPartType.Torso, new Vector3(0f, 0.9f, 0f), new Vector3(20f, 0f, 0f));
        Prim(torso.transform, PrimitiveType.Capsule, new Vector3(0f, 0.33f, 0f), new Vector3(0.75f, 0.42f, 0.6f), Vector3.zero, body, true);
        for (int i = 0; i < 3; i++) // шипы на спине
            Prim(torso.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f - i * 0.22f, -0.3f - i * 0.03f), new Vector3(0.08f, 0.3f, 0.08f), new Vector3(-35f, 0f, 0f), body, false);
        if (brute) Prim(torso.transform, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(1.15f, 0.28f, 0.62f), Vector3.zero, body, false); // плечи
        parts[(int)BodyPartType.Torso] = torso;

        // Голова
        BodyPart head = Pivot(torso.transform, BodyPartType.Head, new Vector3(0f, 0.75f, 0.12f), new Vector3(-20f, 0f, 0f));
        Prim(head.transform, PrimitiveType.Sphere, new Vector3(0f, 0.15f, 0.15f), new Vector3(0.55f, 0.48f, 0.6f), Vector3.zero, body, true);
        Prim(head.transform, PrimitiveType.Sphere, new Vector3(-0.12f, 0.2f, 0.43f), Vector3.one * 0.1f, Vector3.zero, eye, false);
        Prim(head.transform, PrimitiveType.Sphere, new Vector3(0.12f, 0.2f, 0.43f), Vector3.one * 0.1f, Vector3.zero, eye, false);
        if (brute)
        {
            Prim(head.transform, PrimitiveType.Cube, new Vector3(-0.22f, 0.42f, 0.2f), new Vector3(0.08f, 0.35f, 0.08f), new Vector3(-30f, 0f, -25f), body, false); // рога
            Prim(head.transform, PrimitiveType.Cube, new Vector3(0.22f, 0.42f, 0.2f), new Vector3(0.08f, 0.35f, 0.08f), new Vector3(-30f, 0f, 25f), body, false);
        }
        parts[(int)BodyPartType.Head] = head;

        // Руки-когти (сустав — плечо), тянутся вперёд
        for (int s = -1; s <= 1; s += 2)
        {
            BodyPart arm = Pivot(torso.transform, s < 0 ? BodyPartType.ArmL : BodyPartType.ArmR, new Vector3(0.45f * s, 0.62f, 0.05f), new Vector3(-40f, 0f, -8f * s));
            Prim(arm.transform, PrimitiveType.Cube, new Vector3(0f, -0.17f, 0f), new Vector3(0.15f, 0.37f, 0.15f), Vector3.zero, body, true);   // плечо
            Transform elbow = Bend(arm.transform, "Elbow", new Vector3(0f, -0.34f, 0f));
            Prim(elbow, PrimitiveType.Cube, new Vector3(0f, -0.17f, 0f), new Vector3(0.13f, 0.36f, 0.13f), Vector3.zero, body, true);          // предплечье
            Prim(elbow, PrimitiveType.Cube, new Vector3(0f, -0.36f, 0.1f), new Vector3(0.07f, 0.07f, 0.3f), Vector3.zero, body, false);          // коготь
            arm.bend = elbow;
            parts[(int)arm.type] = arm;
        }

        // Ядовитый мешок плевальщика — на спине. Проткнёшь — взорвётся!
        if (kind == EnemyKind.Spitter)
        {
            BodyPart sack = Pivot(torso.transform, BodyPartType.Sack, new Vector3(0f, 0.45f, -0.42f), Vector3.zero);
            Prim(sack.transform, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.6f, 0.6f, 0.5f), Vector3.zero, glow, true);
            parts[(int)BodyPartType.Sack] = sack;
        }
        return parts;
    }

    static Transform Bend(Transform parent, string name, Vector3 pos)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        return go.transform;
    }

    static BodyPart Pivot(Transform parent, BodyPartType type, Vector3 pos, Vector3 euler)
    {
        GameObject go = new GameObject(type.ToString());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        BodyPart bp = go.AddComponent<BodyPart>();
        bp.type = type;
        bp.baseRotation = go.transform.localRotation;
        return bp;
    }

    // hittable — оставить коллайдер (триггер), по которому попадает клинок
    static void Prim(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Material mat, bool hittable)
    {
        GameObject p = GameObject.CreatePrimitive(type);
        Collider c = p.GetComponent<Collider>();
        if (hittable) c.isTrigger = true;
        else Object.Destroy(c);
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        p.transform.localRotation = Quaternion.Euler(euler);
        if (mat != null) p.GetComponent<Renderer>().sharedMaterial = mat;
    }
}
