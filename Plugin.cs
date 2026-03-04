using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Collections.Generic;
using Lean.Pool;
using DG.Tweening;
using BepInEx.Configuration;
using UnityEngine;
using MyBox;
using Photon.Pun;

namespace OvernightWorkers;

[BepInPlugin("OvernightWorkers", "OvernightWorkers", "2.4.4")]
[HarmonyPatch]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    public static ConfigEntry<bool> consolidateBoxes;
    public static ConfigEntry<bool> sortRacks;
    public static List<RackSlot> modifiedSlots = new List<RackSlot>();
    public static List<Restocker> restockers = new List<Restocker>();
    public static PlayerManager playerManager;
    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        DoTheThing();
        consolidateBoxes = Config.Bind<bool>("OvernightWorkers", "Consolidate boxes in racks", false, "Enabling this option will make all boxes in racks take up less space by combining them together. So if you have a box of 4 and a box of 3, in a box that holds 10, it'll make it 1 box of 7.");
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

        foreach (var key in InventoryManager.Instance.m_DisplayedProducts.Keys)
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
                    foreach (var slot in slots)
                    {
                        if (slot != null && slot.m_Boxes != null && slot.m_Boxes.Count > 0)
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
                            if (lastBox.Data.ProductCount <= 0) continue;
                            sourceSlot = slot;
                            sourceBox = lastBox;
                            break;
                        }

                        if (sourceSlot == null || sourceBox == null) break;
                        int takeCount = System.Math.Min(needed, sourceBox.Data.ProductCount);
                        totalAdded += takeCount;
                        if (sourceBox.Data.ProductCount <= takeCount)
                        {
                            /*                            var boxData = sourceBox.Data;
                                                        sourceSlot.m_Boxes.Remove(sourceBox);
                                                        sourceSlot.m_Data.RackedBoxDatas.Remove(boxData);
                                                        if (sourceSlot.m_Data.BoxCount <= 0) sourceSlot.m_Data.Clear();*/
                            TakeBoxPersonal(sourceBox, sourceSlot);
                            CleanUpBox(sourceBox);
                            sourceSlot.RefreshLabel();
                        }
                        else
                        {
                            
                            sourceBox.Data.ProductCount -= takeCount;
                            
                            sourceBox.RefreshSpawnedProducts();
                            sourceSlot.RefreshLabel();
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
    public static void ConsolidateRacks()
    {
        Dictionary<int, List<Box>> boxesByProduct = new Dictionary<int, List<Box>>();
        List<Box> emptyBoxes = new List<Box>();
        foreach (var pair in RackManager.Instance.m_RackSlots)
        {
            foreach (var slot in pair.Value)
            {
                if (slot == null || slot.m_Boxes == null || slot.m_Boxes.Count == 0) continue;

                foreach (var box in slot.m_Boxes)
                {
                    if (box == null || box.m_Data == null || box.Data.ProductCount <= 0) continue;

                    int productId = box.Data.ProductID;
                    if (productId == 0) continue;

                    if (!boxesByProduct.ContainsKey(productId))
                        boxesByProduct[productId] = new List<Box>();

                    boxesByProduct[productId].Add(box);
                }
            }
        }
        foreach (var pair in boxesByProduct)
        {
            var boxes = pair.Value;

            int totalProducts = 0;
            foreach (var box in boxes) totalProducts += box.Data.ProductCount;

            foreach (var box in boxes)
            {
                int fill = System.Math.Min(totalProducts, box.MaxProductCount);
                box.Data.ProductCount = fill;
                totalProducts -= fill;

                if (fill == 0)
                    emptyBoxes.Add(box);
            }
        }
        foreach (Box box in emptyBoxes)
        {
            RackSlot sourceSlot = box.transform.GetComponentInParent<RackSlot>();
            if (sourceSlot == null) continue;

/*            var boxData = box.Data;
            sourceSlot.m_Boxes.Remove(box);
            sourceSlot.m_Data.RackedBoxDatas.Remove(boxData);
            if (sourceSlot.m_Data.BoxCount <= 0) sourceSlot.m_Data.Clear();*/
            
            
            TakeBoxPersonal(box, sourceSlot);
            CleanUpBox(box);
            sourceSlot.RefreshLabel();

        }
    }
    private static void ResetRackSlotLabels()
    {
        RackManager rMan = RackManager.Instance;
        foreach (var col in rMan.m_RackSlots)
        {
            foreach (RackSlot slot in col.Value)
            {
                slot.RefreshLabel();
            }
        } 
    }
    private static void TakeBoxPersonal(Box box, RackSlot slot)
    {
        if (slot.m_Data == null || slot.m_Data.BoxCount <= 0 || slot.m_Data.BoxID == -1)
        {
            return;
        }
        BoxData data = box.Data;
        slot.m_Boxes.Remove(box);
        slot.m_Data.RackedBoxDatas.Remove(data);
        box.ToggleInstanced(false);
        slot.m_Highlightable.AddOrRemoveRenderer(box.RenderersToHighlight, false);
        InventoryManager.Instance.AddBox(box.Data);
        if (slot.m_Data.BoxCount <= 0)
        {
            slot.m_Data.Clear();
            RackManager.Instance.RemoveRackSlot(slot.m_Data.ProductID, slot);
        }
        if (slot.m_ColliderEnabler != null)
        {
            slot.m_ColliderEnabler.Kill(false);
            box.GetComponent<Collider>().enabled = true;
        }
        slot.m_Label.ProductCount = slot.m_Data.TotalProductCount;
        slot.RePositionBoxes();
    }
    public static void CleanUpBox(Box box)
    {
        /*Log.LogWarning($"{box} sent to trasher. Idk what one I need to see so. {box.BoxID}. {box.Product}. {box.Product.ID}");*/
        playerManager.LocalPlayer.PlayerInteraction.m_CurrentInteractable = MyAlgorithms.As<IInteractable>(box);
        playerManager.LocalPlayer.PlayerInteraction.Interact(false, false);
        playerManager.LocalPlayer.BoxInteraction.ThrowIntoTrashBin();
        UnityEngine.Object.Destroy(box.gameObject);
    }

    [HarmonyPatch(typeof(PlayerManager), "Awake")]
    [HarmonyPostfix]
    public static void PlayerManagerPatch(PlayerManager __instance)
    {
        playerManager = __instance;
    }

    [HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
    [HarmonyPrefix]
    public static void RunRestock()
    {
        RestockStore();
        if(consolidateBoxes.Value) ConsolidateRacks();
    }
    [HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
    [HarmonyPostfix]
    public static void RefreshLabels()
    {
        ResetRackSlotLabels();
    }

    /*    [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        public static void RestockTest(PlayerInteraction __instance)
        {
            if (Input.GetKeyDown(KeyCode.L))
            {
                ResetRackSlotLabels();
                Log.LogInfo("Refreshing Labels");
            }
        }*/


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

