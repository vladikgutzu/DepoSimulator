using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

// Реальные уровни: y=0.0 (пол), y=5.5 (верхняя полка)
// Высоты: boxL=0.672, boxM=0.343, barrel=1.016, kone=0.850,
//         largeCont=1.742, plastic=0.637, palletBoxes=2.209, boxPack=2.305
public class CreateShelfConfigs
{
    const string OUT = "Assets/Prefabs/env/shelf_configs";
    const string ENV = "Assets/Prefabs/env";

    static GameObject P(string n) =>
        AssetDatabase.LoadAssetAtPath<GameObject>($"{ENV}/{n}.prefab");

    // Данные для "случайного" генератора
    static float[] RX  = {0.07f,-0.13f,0.21f,-0.08f,0.17f,-0.19f,0.05f,-0.23f,0.12f,-0.06f,0.18f,-0.14f,0.09f,-0.11f,0.22f,-0.03f};
    static float[] RZ  = {0.11f,-0.07f,0.15f,-0.18f,0.04f,-0.12f,0.20f,-0.09f,0.14f,-0.16f,0.06f,-0.10f,0.19f,-0.05f,0.08f,-0.17f};
    static float[] ROT = {13f,37f,67f,112f,225f,17f,83f,160f,290f,45f,130f,72f,200f,350f,25f,95f,185f,265f,315f,55f,145f,240f};

    static float Rx(int i) => RX[i % RX.Length];
    static float Rz(int i) => RZ[i % RZ.Length];
    static float Rot(int i) => ROT[i % ROT.Length];

    static void I(GameObject root, GameObject prefab, float x, float y, float z, float ry = 0)
    {
        if (prefab == null) return;
        var g = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        g.transform.SetParent(root.transform, false);
        g.transform.localPosition = new Vector3(x, y, z);
        if (ry != 0) g.transform.localEulerAngles = new Vector3(0, ry, 0);
    }

    static void Save(GameObject root, string name)
    {
        PrefabUtility.SaveAsPrefabAsset(root, $"{OUT}/{name}.prefab");
        Object.DestroyImmediate(root);
        Debug.Log($"Saved: {name}");
    }

    // Помещает один предмет с хаотичным смещением
    static void IC(GameObject root, GameObject prefab, float x, float y, float z, int seed)
        => I(root, prefab, x + Rx(seed), y, z + Rz(seed), Rot(seed));

    public static void Execute()
    {
        if (!AssetDatabase.IsValidFolder(OUT))
            AssetDatabase.CreateFolder("Assets/Prefabs/env", "shelf_configs");

        var shelf   = P("shelfLarge_prefab");
        var pBoxes  = P("pallet_boxes_prefab");
        var bPack   = P("pallet_box_pack_prefab");
        var pallet  = P("pallet_prefab");
        var boxL    = P("box_large_prefab");
        var boxM    = P("box_med_prefab");
        var boxLong = P("box_long_flat_prefab");
        var boxWide = P("box_wide_flat_prefab");
        var barrel  = P("barrel_prefab");
        var kone    = P("koneBucket_prefab");
        var lCont   = P("large_container_prefab");
        var plastic = P("plastic_container_prefab");

        // Словарь item → высота
        var H = new Dictionary<GameObject, float> {
            {boxL,0.672f},{boxM,0.343f},{boxLong,0.343f},{boxWide,0.343f},
            {barrel,1.016f},{kone,0.85f},{lCont,1.742f},{plastic,0.637f},
            {pBoxes,2.209f},{bPack,2.305f},{pallet,0.152f}
        };

        // Все обычные предметы (без паллет-паков)
        var mixAll = new[]{ boxL,boxM,boxLong,boxWide,barrel,kone,lCont,plastic,pBoxes };
        var mixBox = new[]{ boxL,boxL,boxM,boxM,boxLong,boxWide,boxM,boxL,boxLong,boxL };
        var mixInd = new[]{ barrel,barrel,kone,kone,lCont,plastic,barrel,kone,plastic,lCont };
        var mixPal = new[]{ pBoxes,bPack,pBoxes,boxL,pBoxes,bPack,pBoxes,pBoxes,bPack,pBoxes };

        // X позиции для рядов (шаг ~0.85-0.9m, 13 позиций на ширину 11.6m)
        float[] xGrid = {-5.5f,-4.7f,-3.9f,-3.1f,-2.3f,-1.5f,-0.6f,0.2f,1.0f,1.8f,2.7f,3.6f,4.5f,5.4f};
        // Z позиции для рядов (4 ряда в глубине ~3.2m)
        float[] zGrid = {-1.5f,-0.7f,0.1f,0.9f};

        ChaosMix(shelf, mixAll, H, xGrid, zGrid, boxL, boxM, pBoxes, bPack, barrel, kone);
        ChaosBoxes(shelf, boxL, boxM, boxLong, boxWide, H, xGrid, zGrid);
        ChaosIndustrial(shelf, barrel, kone, lCont, plastic, boxL, boxM, H, xGrid, zGrid);
        ChaosPallets(shelf, pBoxes, bPack, pallet, boxL, barrel, kone, H, xGrid, zGrid);
        ChaosOverflow(shelf, mixAll, H, xGrid, zGrid, boxL, boxM, barrel, kone, lCont, plastic, pBoxes);
        ChaosStacks(shelf, boxL, boxM, boxLong, boxWide, barrel, kone, lCont, plastic, pBoxes, H, xGrid, zGrid);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("=== 6 chaotic configs done ===");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 1: ВСЁ ВПЕРЕМЕШКУ — максимальное разнообразие предметов хаотично
    // ════════════════════════════════════════════════════════════════════
    static void ChaosMix(GameObject shelf,
        GameObject[] mix, Dictionary<GameObject,float> H,
        float[] xs, float[] zs,
        GameObject boxL, GameObject boxM, GameObject pBoxes,
        GameObject bPack, GameObject barrel, GameObject kone)
    {
        var root = new GameObject("shelf_chaos_everything");
        I(root, shelf, 0, 0, 0);

        // Последовательность хаотичных предметов (12 типов вперемешку)
        // Используем разные типы по индексу
        GameObject[] seq = {
            boxL, barrel, boxM, pBoxes, kone, boxL, bPack, boxM, barrel, boxL, kone, boxM,
            pBoxes, barrel, boxL, kone, boxM, bPack, barrel, boxL, pBoxes, kone, boxM, barrel
        };

        // ПОЛА (y=0): заполняем 4 ряда × 14 позиций
        int idx = 0;
        foreach (float z in zs)
        {
            foreach (float x in xs)
            {
                var item = seq[idx % seq.Length];
                float yBase = 0f;
                // Если pBoxes/bPack не вмещаются по z зазору — заменяем на boxL
                if ((item == pBoxes || item == bPack) && z > 0.3f) item = boxL;
                IC(root, item, x, yBase, z, idx);
                // Каждый 3-й: стак поверх
                if (idx % 3 == 0)
                {
                    var top = seq[(idx+7) % seq.Length];
                    if (top == pBoxes || top == bPack) top = boxM;
                    IC(root, top, x, yBase + H[item], z, idx+11);
                    // Каждый 9-й: ещё один слой
                    if (idx % 9 == 0) IC(root, boxM, x, yBase+H[item]+H[top], z, idx+5);
                }
                idx++;
            }
        }

        // ВЕРХ (y=5.5): похожий хаос, смещённые индексы
        float y1 = 5.5f;
        idx = 5;
        foreach (float z in zs)
        {
            foreach (float x in xs)
            {
                var item = seq[(idx+13) % seq.Length];
                if (item == pBoxes || item == bPack) item = barrel;
                IC(root, item, x, y1, z, idx+3);
                if (idx % 4 == 0)
                {
                    var top = seq[(idx+4) % seq.Length];
                    if (top == pBoxes || top == bPack) top = boxM;
                    IC(root, top, x, y1+H[item], z, idx+9);
                }
                idx++;
            }
        }
        Save(root, "shelf_chaos_everything");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 2: КОРОБОЧНАЯ ГОРА — только коробки, очень плотно, горой
    // Микс box_large/box_med/long/wide в хаотичных стэках 1-4 высотой
    // ════════════════════════════════════════════════════════════════════
    static void ChaosBoxes(GameObject shelf, GameObject boxL, GameObject boxM,
                            GameObject boxLong, GameObject boxWide,
                            Dictionary<GameObject,float> H,
                            float[] xs, float[] zs)
    {
        var root = new GameObject("shelf_chaos_box_mountain");
        I(root, shelf, 0, 0, 0);

        // Микс коробок в хаотичном порядке
        GameObject[] boxes = {boxL,boxM,boxWide,boxLong,boxM,boxL,boxLong,boxM,boxWide,boxL,boxM,boxLong};
        // Количество слоёв на каждой позиции (1-4)
        int[] layers = {3,1,4,2,3,1,2,4,3,2,1,3,4,2,3,1};

        int idx = 0;
        foreach (float z in zs)
        {
            // Для пола — плотнее z (добавляем дополнительные ряды между)
            float[] zExtra = {z - 0.35f, z, z + 0.35f};
            foreach (float ze in zExtra)
            {
                if (ze < -1.8f || ze > 1.4f) continue;
                foreach (float x in xs)
                {
                    float y = 0f;
                    int nLayers = layers[idx % layers.Length];
                    for (int l = 0; l < nLayers; l++)
                    {
                        var box = boxes[(idx + l*3) % boxes.Length];
                        IC(root, box, x, y, ze, idx + l*5);
                        y += H[box];
                    }
                    idx++;
                }
            }
        }

        // ВЕРХ — то же самое
        float y1 = 5.5f;
        idx = 3;
        foreach (float z in new[]{-1.5f,-0.9f,-0.3f,0.3f,0.9f})
        {
            foreach (float x in xs)
            {
                float y = y1;
                int nLayers = layers[(idx+2) % layers.Length];
                nLayers = Mathf.Min(nLayers, 3); // верхний уровень — макс 3 слоя до потолка
                for (int l = 0; l < nLayers; l++)
                {
                    var box = boxes[(idx+l) % boxes.Length];
                    IC(root, box, x, y, z, idx+l*7);
                    y += H[box];
                }
                idx++;
            }
        }
        Save(root, "shelf_chaos_box_mountain");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 3: ПРОМЫШЛЕННЫЙ ХАО — бочки, вёдра, контейнеры вперемешку
    // Часть предметов "навалена" — повёрнуты произвольно, стэки
    // ════════════════════════════════════════════════════════════════════
    static void ChaosIndustrial(GameObject shelf,
        GameObject barrel, GameObject kone, GameObject lCont, GameObject plastic,
        GameObject boxL, GameObject boxM, Dictionary<GameObject,float> H,
        float[] xs, float[] zs)
    {
        var root = new GameObject("shelf_chaos_industrial");
        I(root, shelf, 0, 0, 0);

        GameObject[] ind = {barrel,kone,lCont,plastic,barrel,barrel,kone,plastic,lCont,kone,barrel,plastic};
        // Дополнительные маленькие предметы для заполнения щелей
        GameObject[] small = {boxM,plastic,boxM,kone,boxM,plastic,boxM};

        int idx = 0;
        foreach (float z in zs)
        {
            foreach (float x in xs)
            {
                var main = ind[idx % ind.Length];
                IC(root, main, x, 0f, z, idx);

                // Ставим маленький предмет поверх большого
                if (idx % 2 == 0)
                {
                    IC(root, small[idx % small.Length], x, H[main], z, idx+8);
                    // Иногда ещё один сверху
                    if (idx % 6 == 0)
                        IC(root, boxM, x, H[main]+0.343f, z, idx+13);
                }
                // Иногда дополнительный предмет рядом (очень близко)
                if (idx % 5 == 0 && x < 4.5f)
                    IC(root, ind[(idx+3) % ind.Length], x+0.55f, 0f, z, idx+17);

                idx++;
            }
        }

        // ВЕРХ: хаотично всё вперемешку
        float y1 = 5.5f;
        idx = 7;
        // Более плотные x позиции наверху
        float[] xDense = {-5.5f,-4.5f,-3.5f,-2.7f,-1.9f,-1.1f,-0.3f,0.5f,1.3f,2.1f,2.9f,3.7f,4.6f,5.4f};
        foreach (float z in new[]{-1.4f,-0.6f,0.2f,1.0f})
        {
            foreach (float x in xDense)
            {
                var item = ind[idx % ind.Length];
                IC(root, item, x, y1, z, idx+2);
                if (idx % 3 == 0) IC(root, small[(idx+1)%small.Length], x, y1+H[item], z, idx+5);
                idx++;
            }
        }
        Save(root, "shelf_chaos_industrial");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 4: ПАЛЛЕТНЫЙ БЕЗ ПОРЯДКА — паллеты, пак-коробки, pallet_boxes всюду
    // Пол: bPack/pBoxes чередованием с коробками в щелях
    // Верх: pBoxes плотно + barrel/kone между ними
    // ════════════════════════════════════════════════════════════════════
    static void ChaosPallets(GameObject shelf,
        GameObject pBoxes, GameObject bPack, GameObject pallet,
        GameObject boxL, GameObject barrel, GameObject kone,
        Dictionary<GameObject,float> H, float[] xs, float[] zs)
    {
        var root = new GameObject("shelf_chaos_pallets");
        I(root, shelf, 0, 0, 0);

        // pBoxes шириной 1.16m, bPack шириной 3.25m
        // На 11.6m: 3 bPack (9.75m) + щели заполнены pBoxes/boxes
        float y0 = 0f;
        // 3 больших bPack на полу (занимают ~9.75m, центрированы)
        I(root, bPack, -4.1f, y0, -0.1f, 10f);
        I(root, bPack,  0.0f, y0, -0.2f, 355f);
        I(root, bPack,  4.1f, y0,  0.0f,  5f);

        // Заполняем щели между bPack и по бокам pBoxes
        I(root, pBoxes, -5.8f, y0, -0.3f, 90f);
        I(root, pBoxes,  5.8f, y0,  0.2f, 270f);
        // За bPack (z > 1.2) — pBoxes и pallet
        I(root, pBoxes, -3.8f, y0, 1.4f, 15f);
        I(root, pBoxes,  0.3f, y0, 1.4f, 5f);
        I(root, pBoxes,  4.2f, y0, 1.4f, 350f);
        // Поддоны под/рядом — пустые
        I(root, pallet, -5.5f, y0, 1.5f, 20f);
        I(root, pallet,  5.5f, y0, 1.5f, 340f);
        // Коробки и бочки в хаосе рядом
        I(root, boxL,   -5.2f, y0, 0.7f, 37f);
        I(root, barrel,  -5.2f, y0, -0.8f);
        I(root, barrel,   5.2f, y0, -0.8f, 180f);
        I(root, boxL,    5.2f, y0,  0.7f, 200f);
        // boxL поверх bPack (имитирует что докинули)
        I(root, boxL, -4.1f, y0 + H[bPack], -0.1f, 50f);
        I(root, boxL,  0.0f, y0 + H[bPack],  0.3f, 130f);
        I(root, kone,   4.1f, y0 + H[bPack],  0.0f, 80f);

        // ВЕРХ (y=5.5): pBoxes максимально плотно + barrel/kone/boxL в щелях
        float y1 = 5.5f;
        // 8 pBoxes (1.16m ширина) на 11.6m + смещения
        float[] xPal = {-5.2f,-4.0f,-2.8f,-1.6f,-0.4f,0.8f,2.0f,3.2f,4.5f,5.6f};
        float[] zPal = {-1.0f, 0.3f};
        int pi = 0;
        foreach (float z in zPal)
            foreach (float x in xPal)
            {
                IC(root, pBoxes, x, y1, z, pi);
                pi++;
            }
        // Между рядами: barrel, kone, boxL
        float[] xFill = {-4.6f,-2.2f,0.2f,2.6f,5.0f};
        foreach (float x in xFill) IC(root, barrel, x, y1, -0.35f, pi++);
        foreach (float x in xFill) IC(root, kone,   x, y1,  0.0f,  pi++);
        // Стэки pBoxes на pBoxes в 2 местах
        I(root, pBoxes, -4.0f, y1 + H[pBoxes], -1.0f, 20f);
        I(root, pBoxes,  3.2f, y1 + H[pBoxes],  0.3f, 345f);

        Save(root, "shelf_chaos_pallets");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 5: ПЕРЕПОЛНЕНИЕ — предметы "выпадают" за края стеллажа
    // Макс плотность + предметы снаружи у основания стеллажа
    // ════════════════════════════════════════════════════════════════════
    static void ChaosOverflow(GameObject shelf,
        GameObject[] mix, Dictionary<GameObject,float> H,
        float[] xs, float[] zs,
        GameObject boxL, GameObject boxM, GameObject barrel,
        GameObject kone, GameObject lCont, GameObject plastic,
        GameObject pBoxes)
    {
        var root = new GameObject("shelf_chaos_overflow");
        I(root, shelf, 0, 0, 0);

        // Плотная сетка предметов всё вперемешку
        GameObject[] seq = {
            boxL,boxM,barrel,kone,lCont,plastic,boxL,boxM,barrel,boxL,
            plastic,boxM,kone,barrel,boxL,boxM,plastic,kone,barrel,boxM,
            lCont,boxL,boxM,barrel,kone,plastic,boxL,barrel,boxM,kone
        };

        // Пол: ещё более плотная сетка (шаг 0.75m)
        float[] xDense = {-5.6f,-4.85f,-4.1f,-3.35f,-2.6f,-1.85f,-1.1f,-0.35f,0.4f,1.15f,1.9f,2.65f,3.4f,4.15f,4.9f,5.6f};
        int idx = 0;
        foreach (float z in new[]{-1.6f,-0.85f,-0.1f,0.65f,1.3f})
        {
            foreach (float x in xDense)
            {
                var item = seq[idx % seq.Length];
                if (item == pBoxes && z > 0.8f) item = kone;
                IC(root, item, x, 0f, z, idx);
                if (idx % 4 == 0) IC(root, seq[(idx+6)%seq.Length], x, H[item], z, idx+13);
                if (idx % 8 == 0) IC(root, boxM, x, H[item]+H[boxM], z, idx+19);
                idx++;
            }
        }

        // ПРЕДМЕТЫ "ВЫПАВШИЕ" — снаружи за z-краем (z > 2.0 и z < -2.2)
        // Имитирует переполнение полки
        float[] xSpill = {-5f,-3.5f,-2f,-0.5f,1f,2.5f,4f,5.5f};
        foreach (float x in xSpill)
        {
            I(root, seq[idx % seq.Length], x, 0f, 2.2f, Rot(idx)); idx++;
            I(root, boxM, x+0.3f, 0f, 2.6f, Rot(idx)); idx++;
        }
        // Предметы за задней стеной
        float[] xBack = {-4.5f,-1.5f,1.5f,4.5f};
        foreach (float x in xBack)
        {
            I(root, barrel, x, 0f, -2.3f, Rot(idx)); idx++;
        }

        // ВЕРХ: такая же плотность
        float y1 = 5.5f;
        idx = 11;
        foreach (float z in new[]{-1.5f,-0.8f,-0.1f,0.6f})
        {
            foreach (float x in xDense)
            {
                var item = seq[(idx+4) % seq.Length];
                if (item == pBoxes) item = barrel;
                IC(root, item, x, y1, z, idx+2);
                if (idx % 3 == 0) IC(root, boxM, x, y1+H[item], z, idx+15);
                idx++;
            }
        }
        Save(root, "shelf_chaos_overflow");
    }

    // ════════════════════════════════════════════════════════════════════
    // Config 6: ВЫСОКИЕ СТЭКИ — горы из разных предметов, насколько возможно высоко
    // Цель: заполнить пространство от y=0 до ~4.5m (ниже верхней полки)
    // и от y=5.5 до ~8m — максимальные башни
    // ════════════════════════════════════════════════════════════════════
    static void ChaosStacks(GameObject shelf,
        GameObject boxL, GameObject boxM, GameObject boxLong, GameObject boxWide,
        GameObject barrel, GameObject kone, GameObject lCont, GameObject plastic,
        GameObject pBoxes, Dictionary<GameObject,float> H,
        float[] xs, float[] zs)
    {
        var root = new GameObject("shelf_chaos_high_stacks");
        I(root, shelf, 0, 0, 0);

        // Стэки-башни разных составов
        // Башня 1: lCont → plastic×2 → boxL×2 = 1.74+0.64+0.64+0.67+0.67 = 4.36m ✓
        // Башня 2: barrel → barrel → kone → boxL = 1.02+1.02+0.85+0.67 = 3.56m ✓
        // Башня 3: pBoxes → kone → boxM×2 = 2.21+0.85+0.34+0.34 = 3.74m ✓
        // Башня 4: barrel → lCont → boxM = 1.02+1.74+0.34 = 3.10m ✓
        // Башня 5: plastic×3 → boxL = 0.64×3+0.67 = 2.59m ✓
        // Башня 6: boxL×4 + boxM×2 = 0.67×4+0.34×2 = 3.36m ✓
        // Башня 7: kone×3 → boxM = 0.85×3+0.34 = 2.89m ✓

        // Паттерны башен: список (item, количество) или фиксированный порядок
        void Tower(float x, float z, float[] heights, GameObject[] items, int seed)
        {
            float y = 0f;
            for (int i = 0; i < items.Length; i++)
            {
                IC(root, items[i], x, y, z, seed+i*3);
                y += H[items[i]];
            }
        }

        // Нижний уровень — расставляем башни плотно
        // Шаблоны башен
        var t1 = new[]{lCont, plastic, plastic, boxL, boxL};
        var t2 = new[]{barrel, barrel, kone, boxL};
        var t3 = new[]{pBoxes, kone, boxM, boxM};
        var t4 = new[]{barrel, lCont, boxM, boxM};
        var t5 = new[]{plastic, plastic, plastic, boxL, boxM};
        var t6 = new[]{boxL, boxL, boxL, boxL, boxM, boxM};
        var t7 = new[]{kone, kone, kone, boxL};
        var t8 = new[]{barrel, plastic, kone, boxL, boxM};
        GameObject[][] towers = {t1,t2,t3,t4,t5,t6,t7,t8,t2,t1,t6,t3,t7,t4,t8,t5};

        // Расставляем башни по сетке (шаг ~0.85m)
        float[] xT = {-5.5f,-4.6f,-3.7f,-2.8f,-1.9f,-1.0f,-0.1f,0.8f,1.7f,2.6f,3.5f,4.4f,5.3f};
        float[] zT = {-1.4f,-0.5f,0.4f};
        int ti = 0;
        foreach (float z in zT)
        foreach (float x in xT)
        {
            Tower(x, z, null, towers[ti % towers.Length], ti*3);
            ti++;
        }

        // Дополнительные одиночные предметы между башнями (заполнение щелей)
        float[] xGap = {-5.1f,-3.2f,-1.4f,0.4f,2.2f,4.0f,5.8f};
        int gi = 5;
        foreach (float x in xGap)
        {
            IC(root, boxM, x,  0f,  -1.0f, gi); gi++;
            IC(root, boxM, x,  0.343f, -1.0f, gi); gi++;
            IC(root, kone, x,  0f,  0.0f,  gi); gi++;
            IC(root, boxL, x,  0f,  0.9f,  gi); gi++;
        }

        // ВЕРХ (y=5.5): такие же башни, но макс 2.8m высотой (до ~8.3m)
        var u1 = new[]{boxL, boxL, boxL, boxM, boxM}; // 0.67×3+0.34×2 = 2.69
        var u2 = new[]{barrel, kone, boxL}; // 1.02+0.85+0.67 = 2.54
        var u3 = new[]{lCont, boxM, boxM, boxM}; // 1.74+0.34×3 = 2.76
        var u4 = new[]{plastic, plastic, boxL, boxM, boxM}; // 0.64×2+0.67+0.34×2 = 2.63
        var u5 = new[]{kone, kone, boxL, boxM}; // 0.85×2+0.67+0.34 = 2.71
        var u6 = new[]{barrel, plastic, boxL, boxM}; // 1.02+0.64+0.67+0.34 = 2.67
        GameObject[][] utowers = {u1,u2,u3,u4,u5,u6,u2,u1,u5,u3,u6,u4,u1,u3};

        float y1 = 5.5f;
        int ui = 2;
        foreach (float z in new[]{-1.4f,-0.6f,0.2f,1.0f})
        foreach (float x in xT)
        {
            var tow = utowers[ui % utowers.Length];
            float y = y1;
            for (int k = 0; k < tow.Length; k++)
            {
                IC(root, tow[k], x, y, z, ui+k*4);
                y += H[tow[k]];
            }
            ui++;
        }

        Save(root, "shelf_chaos_high_stacks");
    }
}
