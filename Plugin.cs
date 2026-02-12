using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Collections.Generic;
using Lean.Pool;
using BepInEx.Configuration;
using System.Collections;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using BepInEx.Bootstrap;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using System.Threading.Tasks;

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
        WorkBitch();
        Log.LogInfo($"Plugin OvernightWorkers is loaded!");
    }


    
    public static void WorkBitch()
    {
        Harmony workBitch = new Harmony("com.sms.apollo.overnightworkers");
        workBitch.PatchAll();
    }

    public static void RestockStore()
    {

        var productsInInventory = new List<int>();
        foreach (var key in InventoryManager.Instance.Products.Keys)
        {
            if (key != 0) productsInInventory.Add(key);
        }


        /*        Log.LogWarning($"[Restock] RackManager.m_RackSlots has {RackManager.Instance.m_RackSlots.Count} product IDs");
                foreach (var pair in RackManager.Instance.m_RackSlots)
                {
                    Log.LogWarning($"[Restock] Product ID {pair.Key} has {pair.Value.Count} rack slots");
                }*/


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

                    if (newList.Count > 0)
                    {
                        indexedRackSlots[pair.Key] = newList;
                        /*Log.LogWarning($"[Restock] Indexed product {pair.Key} with {newList.Count} valid slots");*/
                    }
                    /*                    else
                                        {
                                            Log.LogWarning($"[Restock] Product {pair.Key} had slots but all were filtered out");
                                        }*/
                }


                var displaySlots = new Il2CppSystem.Collections.Generic.List<DisplaySlot>();
                DisplayManager.Instance.GetDisplaySlots(id, false, displaySlots);

                foreach (var displaySlot in displaySlots)
                {
                    if (displaySlot == null) continue;

                    int maxCount = productSO.GridLayoutInStorage.productCount;
                    int currentCount = displaySlot.m_Products.Count;
                    int needed = maxCount - currentCount;

                    /*Log.LogWarning($"[Restock] Product {id}: Current={currentCount}, Max={maxCount}, Needed={needed}");*/

                    if (needed <= 0) continue;


                    if (!indexedRackSlots.TryGetValue(id, out var slots))
                    {
                        /*Log.LogWarning($"[Restock] Product {id} not found in indexedRackSlots at all");*/
                        continue;
                    }

                    if (slots.Count == 0)
                    {
                        /*Log.LogWarning($"[Restock] Product {id} has 0 slots in indexedRackSlots");*/
                        continue;
                    }

                    /*Log.LogWarning($"[Restock] Product {id} has {slots.Count} rack slots available");*/


                    int slotsWithBoxes = 0;
                    foreach (var s in slots)
                    {
                        if (s != null && s.m_Boxes != null && s.m_Boxes.Count > 0)
                        {
                            slotsWithBoxes++;
                            /*Log.LogWarning($"[Restock] Product {id}: Slot has {s.m_Boxes.Count} boxes, HasProduct={s.HasProduct}");*/
                        }
                    }
                    /*Log.LogWarning($"[Restock] Product {id}: {slotsWithBoxes} slots have boxes");*/

                    int totalAdded = 0;


                    while (needed > 0)
                    {
                        RackSlot sourceSlot = null;
                        Box sourceBox = null;


                        foreach (var slot in slots)
                        {
                            if (slot == null || !slot.HasProduct)
                                continue;

                            if (slot.m_Boxes == null || slot.m_Boxes.Count == 0)
                                continue;

                            var lastBox = slot.m_Boxes[slot.m_Boxes.Count - 1];
                            if (lastBox == null || lastBox.m_Data == null)
                                continue;

                            if (lastBox.m_Data.ProductCount <= 0)
                                continue;

                            sourceSlot = slot;
                            sourceBox = lastBox;
                            break;
                        }


                        if (sourceSlot == null || sourceBox == null)
                        {
                            /*Log.LogWarning($"[Restock] No boxes found for product {id}, needed {needed} more");*/
                            break;
                        }


                        int takeCount = System.Math.Min(needed, sourceBox.m_Data.ProductCount);

                        /* Log.LogWarning($"[Restock] Taking {takeCount} from box {sourceBox.BoxID} (has {sourceBox.m_Data.ProductCount})");*/

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

                        foreach (var product in displaySlot.m_Products)
                        {
                            if (product != null)
                            {
                                LeanPool.Despawn(product);
                            }
                        }
                        displaySlot.m_Products.Clear();

                        displaySlot.Data.FirstItemCount = newTotal;
                        displaySlot.SpawnProduct(id, newTotal);

                        /*                        Log.LogWarning($"[Restock] Spawned {newTotal} products. Actual: {displaySlot.m_Products.Count}");*/

                        displaySlot.SetLabel();
                        displaySlot.SetPriceTag();
                    }
                }
            }
        }
        Log.LogWarning("[RestockStore] Store Restocking Complete");
    }

    /*   public static void DeleteAllBoxes()
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
               // Use while loop instead of foreach - keeps removing until empty
               while (slot.Boxes.Count > 0)
               {
                   Box box = slot.Boxes[0]; // Always take the first box
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
       }*/

    [HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
    [HarmonyPostfix]
    public static void RunRestock()
    {
        RestockStore();
    }

/*    [HarmonyPatch(typeof(DisplayManager), "AddDisplaySlot")]
    [HarmonyPrefix]
    public static bool DMContainsProduct(DisplayManager __instance, int productID, DisplaySlot newSlot)
    {
        if (__instance.m_DisplayedProducts.ContainsKey(productID))
        {
            if (__instance.m_DisplayedProducts[productID].Contains(newSlot))
            {
                return false;
            }
        }
        return true;
    }*/
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

    //debug, leaving just in case but commented out
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
    public static void RestockTest2(PlayerInteraction __instance)
    {
        if (Input.GetKeyDown(KeyCode.K))
        {
            ConsolidateRacks();
            Log.LogInfo("Consolidate Racks ran");
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
    public static void RestockTest3(PlayerInteraction __instance)
    {
        if (Input.GetKeyDown(KeyCode.J))
        {
            SortRacks();
            Log.LogInfo("Sort Racks ran");
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

