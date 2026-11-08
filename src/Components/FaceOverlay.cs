using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cavi.ChillWithAnyone.Components
{
    internal sealed class FaceOverlay : MonoBehaviour
    {
        private static FaceOverlay active;
        private OverlayPass pass;

        internal static void Attach(GameObject character, SkinnedMeshRenderer face, SkinnedMeshRenderer hair, AssetBundle bundle)
        {
            var metadata = bundle.LoadAsset<TextAsset>("DeepSeekFaceOverlaySlots");
            if (!metadata)
                return;
            var shader = bundle.LoadAsset<Shader>("DeepSeekFaceOverlay");
            if (!hair || !shader || !shader.isSupported || shader.passCount != 4)
            {
                Utils.ModLogger.Warning("Face overlay: missing hair renderer or supported mask shader.");
                return;
            }

            try
            {
                using var reader = new BinaryReader(new MemoryStream(metadata.bytes));
                var sourceMaterials = face.sharedMaterials;
                int count = reader.ReadInt32();
                if (count < 1 || count > sourceMaterials.Length || metadata.bytes.Length != sizeof(int) * (count + 1))
                    throw new InvalidDataException("Invalid face overlay submesh count.");
                var slots = new int[count];
                for (int i = 0; i < slots.Length; i++)
                {
                    int slot = reader.ReadInt32();
                    if (slot < 0 || slot >= sourceMaterials.Length || !sourceMaterials[slot])
                        throw new InvalidDataException("Invalid face overlay submesh index.");
                    slots[i] = slot;
                }
                var overlay = character.AddComponent<FaceOverlay>();
                overlay.pass = new OverlayPass(face, hair, slots, shader);
                active = overlay;
                Utils.ModLogger.Info($"Face overlay enabled for {count} submeshes at 50% opacity.");
            }
            catch (IOException ex)
            {
                Utils.ModLogger.Warning("Face overlay: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            if (active == this)
                active = null;
            pass?.Dispose();
        }

        [HarmonyPatch(typeof(ScriptableRenderer), "AddRenderPasses")]
        internal static class RenderPassPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ScriptableRenderer __instance, ref RenderingData renderingData)
            {
                if (active && active.pass != null && active.pass.IsVisibleTo(renderingData.cameraData))
                    __instance.EnqueuePass(active.pass);
            }
        }

        internal sealed class OverlayPass : ScriptableRenderPass, IDisposable
        {
            private static readonly int HairMask = Shader.PropertyToID("_DeepSeekHairMask");
            private readonly SkinnedMeshRenderer face;
            private readonly SkinnedMeshRenderer hair;
            private readonly int[] slots;
            private readonly Material passMaterial;
            private readonly Material[] featureMaterials;
            private RTHandle mask;
            private RTHandle features;
            private RTHandle faceDepth;

            public OverlayPass(SkinnedMeshRenderer face, SkinnedMeshRenderer hair, int[] slots, Shader shader)
            {
                this.face = face;
                this.hair = hair;
                this.slots = slots;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);

                passMaterial = CoreUtils.CreateEngineMaterial(shader);
                var source = face.sharedMaterials;
                featureMaterials = new Material[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    var feature = CoreUtils.CreateEngineMaterial(shader);
                    feature.SetTexture("_OverlayMap", source[slots[i]].GetTexture("_MainTex"));
                    feature.SetColor("_OverlayTint", source[slots[i]].GetColor("_Color"));
                    featureMaterials[i] = feature;
                }
            }

            public bool IsVisibleTo(CameraData data)
            {
                if (data.cameraType != CameraType.Game || data.renderType != CameraRenderType.Base ||
                    !face || !hair || !face.enabled || !hair.enabled || face.forceRenderingOff || hair.forceRenderingOff ||
                    !face.gameObject.activeInHierarchy || !hair.gameObject.activeInHierarchy)
                    return false;
                int mask = data.camera.cullingMask;
                return (mask & (1 << face.gameObject.layer)) != 0 && (mask & (1 << hair.gameObject.layer)) != 0;
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                var descriptor = data.cameraData.cameraTargetDescriptor;
                descriptor.graphicsFormat = GraphicsFormat.R8_UNorm;
                descriptor.depthBufferBits = 0;
                descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.msaaSamples = 1;
                descriptor.bindMS = false;
                descriptor.memoryless = RenderTextureMemoryless.None;
                RenderingUtils.ReAllocateIfNeeded(ref mask, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_DeepSeekHairMask");
                descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                RenderingUtils.ReAllocateIfNeeded(ref features, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_DeepSeekFaceFeatures");
                descriptor.graphicsFormat = GraphicsFormat.None;
                descriptor.depthStencilFormat = GraphicsFormatUtility.GetDepthStencilFormat(24, 0);
                RenderingUtils.ReAllocateIfNeeded(ref faceDepth, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_DeepSeekFaceDepth");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var cmd = CommandBufferPool.Get("DeepSeek hair mask and face overlay");
                try
                {
                    CoreUtils.SetRenderTarget(cmd, mask, ClearFlag.Color, Color.black);
                    for (int slot = 0; slot < hair.sharedMesh.subMeshCount; slot++)
                        cmd.DrawRenderer(hair, passMaterial, slot, 0);

                    // Preserve occlusion by the face and eyelids without including hair in this depth buffer.
                    CoreUtils.SetRenderTarget(cmd, features, faceDepth, ClearFlag.All, Color.clear);
                    for (int slot = 0; slot < face.sharedMesh.subMeshCount; slot++)
                        cmd.DrawRenderer(face, passMaterial, slot, 1);
                    cmd.SetGlobalTexture(HairMask, mask.nameID);
                    for (int i = 0; i < slots.Length; i++)
                        cmd.DrawRenderer(face, featureMaterials[i], slots[i], 2);

                    Blitter.BlitCameraTexture(cmd, features, data.cameraData.renderer.cameraColorTargetHandle,
                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, passMaterial, 3);
                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }

            public void Dispose()
            {
                mask?.Release();
                features?.Release();
                faceDepth?.Release();
                CoreUtils.Destroy(passMaterial);
                foreach (var material in featureMaterials)
                    CoreUtils.Destroy(material);
            }
        }
    }
}
