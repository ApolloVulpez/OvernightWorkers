using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Collections.Generic;
using Lean.Pool;
using BepInEx.Configuration;
using UnityEngine;

namespace OvernightWorkers;

[BepInPlugin("OvernightWorkers", "OvernightWorkers", "2.0.0")]
[HarmonyPatch]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    public static ConfigEntry<bool> consolidateBoxes;
    public static ConfigEntry<bool> sortRacks;
    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        DoTheThing();
        Log.LogInfo($"Plugin OvernightWorkers is loaded!");
    }
    public static void DoTheThing()
    {
        Harmony doTheThing = new Harmony("com.sms.apollo.overnightworkers");
        doTheThing.PatchAll();
    }

    public static void RestockStore()
    {
        var productsInInventory = new List<int>();

        foreach (var key in InventoryManager.Instance.Products.Keys)
        {
            if (key != 0) productsInInventory.Add(key);
        }

        foreach (var id in productsInInventory)
        {
            var productSO = IDManager.Instance.ProductSO(id);
            if (productSO == null) continue;
            bool productNeedsMore = true;

            while (productNeedsMore)
            {
                productNeedsMore = false;
                var indexedRackSlots = new Dictionary<int, List<RackSlot>>();
                foreach (var pair in RackManager.Instance.m_RackSlots)
                {
                    var newList = new List<RackSlot>();
                    foreach (var slot in pair.Value)
                    {
                        if (slot.Boxes == null) continue;
                        newList.Add(slot);
                    }
                    if (newList.Count > 0) indexedRackSlots[pair.Key] = newList;
                }

                var displaySlots = new Il2CppSystem.Collections.Generic.List<DisplaySlot>();
                DisplayManager.Instance.GetDisplaySlots(id, false, displaySlots);

                foreach (var displaySlot in displaySlots)
                {
                    if (displaySlot == null) continue;
                    int maxCount = productSO.GridLayoutInStorage.productCount;
                    int currentCount = displaySlot.m_Products.Count;
                    int needed = maxCount - currentCount;
                    if (needed <= 0) continue;
                    if (!indexedRackSlots.TryGetValue(id, out var slots)) continue;
                    if (slots.Count == 0) continue;
                    int slotsWithBoxes = 0;
                    foreach (var s in slots)
                    {
                        if (s != null && s.m_Boxes != null && s.m_Boxes.Count > 0)
                        {
                            slotsWithBoxes++;
                        }
                    }
                    int totalAdded = 0;

                    while (needed > 0)
                    {
                        RackSlot sourceSlot = null;
                        Box sourceBox = null;
                        foreach (var slot in slots)
                        {
                            if (slot == null || !slot.HasProduct) continue;
                            if (slot.m_Boxes == null || slot.m_Boxes.Count == 0) continue;
                            var lastBox = slot.m_Boxes[slot.m_Boxes.Count - 1];
                            if (lastBox == null || lastBox.m_Data == null) continue;
                            if (lastBox.m_Data.ProductCount <= 0) continue;
                            sourceSlot = slot;
                            sourceBox = lastBox;
                            break;
                        }

                        if (sourceSlot == null || sourceBox == null) break;
                        int takeCount = System.Math.Min(needed, sourceBox.m_Data.ProductCount);
                        totalAdded += takeCount;
                        if (sourceBox.m_Data.ProductCount <= takeCount)
                        {
                            sourceSlot.TakeBoxFromRack();
                            InventoryManager.Instance.RemoveBox(sourceBox.Data);
                            LeanPool.Despawn(sourceBox);
                            sourceBox.ResetBox();
                            UnityEngine.Object.Destroy(sourceBox.gameObject);
                        }
                        else
                        {
                            sourceBox.m_Data.ProductCount -= takeCount;
                            sourceBox.DespawnProducts();
                            sourceBox.SpawnProducts();
                        }
                        needed -= takeCount;
                        productNeedsMore = true;
                    }
                    if (totalAdded > 0)
                    {
                        int newTotal = currentCount + totalAdded;
                        foreach (var product in displaySlot.m_Products) if (product != null) LeanPool.Despawn(product);
                        displaySlot.m_Products.Clear();
                        displaySlot.Data.FirstItemCount = newTotal;
                        displaySlot.SpawnProduct(id, newTotal);
                        displaySlot.SetLabel();
                        displaySlot.SetPriceTag();
                    }
                }
            }
        }
        Log.LogWarning("[RestockStore] Store Restocking Complete");
    }



    [HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
    [HarmonyPostfix]
    public static void RunRestock()
    {
        RestockStore();
    }



    //Debug functions
/*    public static void StockHalfer()
    {
        var manager = RackManager.Instance;
        if (manager == null || manager.m_Racks == null)
        {
            Log.LogWarning("[StockHalfer] RackManager not found!");
            return;
        }

        int boxesProcessed = 0;

        foreach (var rack in manager.m_Racks)
        {
            if (rack == null || rack.RackSlots == null) continue;

            foreach (var slot in rack.RackSlots)
            {
                if (slot == null || slot.Boxes == null) continue;

                foreach (var box in slot.Boxes)
                {
                    if (box == null || box.m_Data == null) continue;

                    int currentCount = box.m_Data.ProductCount;
                    if (currentCount <= 0) continue;

                    int newCount = currentCount / 2;

                    Log.LogWarning($"[StockHalfer] Box {box.BoxID}: {currentCount} -> {newCount} products");

                    box.m_Data.ProductCount = newCount;
                    box.DespawnProducts();
                    box.SpawnProducts();

                    boxesProcessed++;
                }

                slot.SetLabel();
            }
        }

        Log.LogWarning($"[StockHalfer] Halved products in {boxesProcessed} boxes");
    }
    public static void DeleteAllBoxes()
    {
        RackManager manager = RackManager.Instance;
        List<RackSlot> slots = new List<RackSlot>();
        var racks = manager.m_Racks;
        if (racks == null)
        {
            Log.LogWarning("[SortRacks] No racks found!");
            return;
        }
        foreach (var rack in racks)
        {
            if (rack == null || rack.RackSlots == null) continue;
            foreach (var slot in rack.RackSlots)
            {
                if (slot != null)
                {
                    slots.Add(slot);
                }
            }
        }

        foreach (RackSlot slot in slots)
        {

            while (slot.Boxes.Count > 0)
            {
                Box box = slot.Boxes[0]; 
                slot.TakeBoxFromRack();
                InventoryManager.Instance.RemoveBox(box.Data);
                box.ResetBox();
                LeanPool.Despawn(box);
                UnityEngine.Object.Destroy(box.gameObject);
                Log.LogWarning($"The while loop in delete boxes is still going"[0]);
            }
            slot.ClearLabel();
        }

        Log.LogWarning("Deleted all boxes from racks");
    }


    [HarmonyPatch(typeof(PlayerInteraction), "Update")]
    [HarmonyPostfix]
    public static void RestockTest(PlayerInteraction __instance)
    {
        if (Input.GetKeyDown(KeyCode.L))
        {
            RestockStore();
            Log.LogInfo("InstantRestock ran");
        }
    }

    [HarmonyPatch(typeof(PlayerInteraction), "Update")]
    [HarmonyPostfix]
    public static void RestockTest6(PlayerInteraction __instance)
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            StockHalfer();
            Log.LogInfo("Consolidate Racks ran");
        }
    }
    [HarmonyPatch(typeof(PlayerInteraction), "Update")]
    [HarmonyPostfix]
    public static void RestockTest4(PlayerInteraction __instance)
    {
        if (Input.GetKeyDown(KeyCode.H))
        {
            DeleteAllBoxes();
            Log.LogInfo("DeletedBoxes");
        }
    }*/
}

