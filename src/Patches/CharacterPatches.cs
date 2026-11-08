using Bulbul;
using Cavi.ChillWithAnyone;
using Cavi.ChillWithAnyone.Components; 
using Cavi.ChillWithAnyone.Utils;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Cavi.ChillWithAnyone.Patches
{
    public static class CharacterPatches
    {
        private static Transform replacementRoot;
        private static SkinnedMeshRenderer replacementFace;
        private static readonly FieldInfo CostumeObjects = AccessTools.Field(typeof(CostumeChangeService), "_costumeObjectList");
        private static readonly FieldInfo CostumeSkeleton = AccessTools.Field(typeof(CostumeChangeService), "_characterBornParent");

        [HarmonyPatch(typeof(RoomGameManager), "Initialize")]
        public static class RoomManagerPatch
        {
            [HarmonyPostfix]
            public static void Postfix(RoomGameManager __instance, CostumeChangeService ____costumeChangeService)
            {
                if (ChillWithAnyonePlugin.IsModelLoaded) return;

                ModLogger.Info("RoomGameManager initialized");
                GameObject characterObj = GameObject.Find("Character");

                if (characterObj != null)
                {
                    ReplaceCharacterModel(characterObj, ____costumeChangeService);
                }
                else
                {
                    TryFindCharacterFromService(__instance, ____costumeChangeService);
                }
            }

            private static void TryFindCharacterFromService(RoomGameManager instance, CostumeChangeService costumeService)
            {
                var fieldInfo = typeof(RoomGameManager).GetField("_heroineService",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (fieldInfo?.GetValue(instance) is MonoBehaviour service)
                {
                    ReplaceCharacterModel(service.gameObject, costumeService);
                }
            }
        }

        [HarmonyPatch(typeof(CostumeChangeService), "AttachCostumeBonesToCharacter")]
        public static class CostumeVisibilityPatch
        {
            [HarmonyPrefix]
            public static void Prefix(CostumeChangeService __instance)
            {
                HideGameCostume(__instance);
            }
        }

        public static void ReplaceCharacterModel(GameObject characterRoot, CostumeChangeService costumeService = null)
        {
            if (ChillWithAnyonePlugin.CustomCharacterPrefab == null || ChillWithAnyonePlugin.IsModelLoaded)
                return;

            ModLogger.Info("Starting character model replacement");

            var originalRenderers = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var targetFaceRenderer = originalRenderers.FirstOrDefault(renderer => renderer.name == "Face");
            Transform rootBone = FindChildRecursive(characterRoot.transform, "Character_Hips");
            if (!targetFaceRenderer || !rootBone)
                return;

            Dictionary<string, Material[]> materials;
            try
            {
                materials = ChillWithAnyonePlugin.NativeMaterials.CreateRendererMaterials(originalRenderers);
            }
            catch (InvalidDataException ex)
            {
                ModLogger.Error("Cannot reuse original character materials: " + ex.Message);
                return;
            }

            // The costume service and animation can toggle enabled or activate a previously
            // inactive object. Suppress rendering independently of both switches.
            replacementRoot = characterRoot.transform;
            replacementFace = targetFaceRenderer;
            int hidden = HideRenderers(originalRenderers);
            var originalExpressionMeshes = FindChildRecursive(replacementRoot, "group9");
            if (originalExpressionMeshes)
                hidden += HideRenderers(originalExpressionMeshes.GetComponentsInChildren<Renderer>(true));

            HideGameCostume(costumeService ?? Object.FindObjectOfType<CostumeChangeService>());
            targetFaceRenderer.enabled = true;
            ModLogger.Info($"Suppressed {hidden} original character renderers, including inactive parts");

            var customRenderers = ChillWithAnyonePlugin.CustomCharacterPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var renderer in customRenderers)
            {
                var bones = RemapBones(renderer, characterRoot.transform, rootBone);
                if (renderer.name == ChillWithAnyonePlugin.BODY_MESH_NAME)
                    InjectFaceMesh(targetFaceRenderer, renderer, materials[renderer.name], bones, rootBone);
                else
                    CreateNewMeshPart(characterRoot, renderer, materials[renderer.name], bones, rootBone);
            }

            ConfigureAccessories(characterRoot);
            AttachBlendShapeLinker(characterRoot);
            var hair = characterRoot.transform.Find("Hair_Mod");
            FaceOverlay.Attach(characterRoot, targetFaceRenderer,
                hair ? hair.GetComponent<SkinnedMeshRenderer>() : null, ChillWithAnyonePlugin.CustomAssetBundle);
            ChillWithAnyonePlugin.SetModelLoaded(true);
            ModLogger.Info("Character model replacement completed with the game's original shaders");
        }

        private static void HideGameCostume(CostumeChangeService service)
        {
            if (!service || !replacementRoot ||
                !(CostumeSkeleton?.GetValue(service) is Transform skeleton) ||
                !(skeleton == replacementRoot || skeleton.IsChildOf(replacementRoot)) ||
                !(CostumeObjects?.GetValue(service) is List<GameObject> objects))
                return;

            int hidden = 0;
            foreach (var costume in objects)
            {
                if (costume)
                    hidden += HideRenderers(costume.GetComponentsInChildren<Renderer>(true));
            }
            if (hidden > 0)
                ModLogger.Info($"Suppressed {hidden} original costume renderers");
        }

        private static int HideRenderers(IEnumerable<Renderer> renderers)
        {
            int hidden = 0;
            foreach (var renderer in renderers)
            {
                if (!renderer || renderer == replacementFace || renderer.forceRenderingOff || IsBookProp(renderer.transform))
                    continue;
                renderer.forceRenderingOff = true;
                hidden++;
            }
            return hidden;
        }

        private static bool IsBookProp(Transform part)
        {
            for (var current = part; current && current != replacementRoot; current = current.parent)
            {
                if (current.name == "m_novel" || current.name == "m_kumares_book")
                    return true;
            }
            return false;
        }

        private static Transform[] RemapBones(
            SkinnedMeshRenderer renderer,
            Transform characterRoot,
            Transform fallbackBone)
        {
            Transform[] bones = new Transform[renderer.bones.Length];

            for (int i = 0; i < renderer.bones.Length; i++)
            {
                string boneName = renderer.bones[i].name;
                Transform foundBone = FindChildRecursive(characterRoot, boneName);
                bones[i] = foundBone ?? fallbackBone;
            }

            return bones;
        }

        private static void InjectFaceMesh(
            SkinnedMeshRenderer target,
            SkinnedMeshRenderer source,
            Material[] materials,
            Transform[] bones,
            Transform rootBone)
        {
            ModLogger.LogInjection("Injecting custom mesh into Face component");

            target.sharedMesh = source.sharedMesh;
            target.sharedMaterials = materials;
            target.bones = bones;
            target.rootBone = rootBone;
        }

        private static void CreateNewMeshPart(
            GameObject parent,
            SkinnedMeshRenderer source,
            Material[] materials,
            Transform[] bones,
            Transform rootBone)
        {
            string partName = $"{source.name}_Mod";
            GameObject newPart = new GameObject(partName);
            newPart.transform.SetParent(parent.transform, false);

            SkinnedMeshRenderer newRenderer = newPart.AddComponent<SkinnedMeshRenderer>();
            newRenderer.sharedMesh = source.sharedMesh;
            newRenderer.sharedMaterials = materials;
            newRenderer.bones = bones;
            newRenderer.rootBone = rootBone;

            ModLogger.Debug($"Created mesh part: {partName}");
        }

        // TODO: how to support different avatars with different accessory names/structures? Using external config?
        private static void ConfigureAccessories(GameObject characterRoot)
        {
            ConfigureGlasses(characterRoot);
            ConfigureHeadphones(characterRoot);
        }

        private static void ConfigureGlasses(GameObject characterRoot)
        {
            Transform glasses = FindChildRecursive(characterRoot.transform, "m_Glasses");
            if (glasses == null) return;

            glasses.gameObject.SetActive(ChillWithAnyonePlugin.EnableGlasses);

            if (ChillWithAnyonePlugin.EnableGlasses)
            {
                glasses.localPosition = new Vector3(-0.008f, 0.008f, 0.012f);
                glasses.localScale = Vector3.one * 1.29f;
            }
            else
            {
                glasses.localPosition = new Vector3(99f, 99f, 99f);
            }
        }

        private static void ConfigureHeadphones(GameObject characterRoot)
        {
            Transform headphones = FindChildRecursive(characterRoot.transform, "m_Headphone_cat");
            if (headphones == null) return;

            // TODO: Determine proper headphone position instead of hiding
            headphones.gameObject.SetActive(false);
            headphones.localPosition = new Vector3(99f, 99f, 99f);
        }

        private static void AttachBlendShapeLinker(GameObject characterRoot)
        {
            var faceRenderer = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>()
                .FirstOrDefault(smr => smr.name == "Face");

            if (faceRenderer != null)
            {
                var linker = characterRoot.AddComponent<BlendShapeLinker>();
               
                linker.originalRenderer = faceRenderer;
                linker.customRenderer = faceRenderer;

                ModLogger.Info("BlendShapeLinker component attached");

                ChillWithAnyonePlugin.AttachDebugger(characterRoot, linker);
            }
            else
            {
                ModLogger.Warning("Face component not found, cannot attach BlendShapeLinker");
            }
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;

            foreach (Transform child in parent)
            {
                Transform result = FindChildRecursive(child, name);
                if (result != null) return result;
            }

            return null;
        }
    }
}



