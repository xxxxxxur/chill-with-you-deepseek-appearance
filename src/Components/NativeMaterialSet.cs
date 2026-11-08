using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Cavi.ChillWithAnyone.Components
{
    internal sealed class NativeMaterialSet
    {
        private static readonly string[] BaseMaps = { "_MainTex", "_BaseMap", "_BaseColorMap" };
        private static readonly string[] BaseColors = { "_Color", "_BaseColor" };
        // The original masks describe the game's mesh UVs. Keep the lighting parameters and use
        // the shader's neutral defaults for the UV-dependent textures on the replacement mesh.
        private static readonly string[] OriginalUvMasks =
        {
            "_BacklightColorTex", "_RimColorTex", "_ShadowBorderMask", "_ReflectionColorTex", "_RimShadeMask"
        };

        private static readonly string[] ContrastFeatures =
        {
            "_UseShadow", "_UseBacklight", "_UseRim", "_UseRimShade",
            "_UseMatCap", "_UseMatCap2nd", "_UseReflection", "_UseGlitter", "_VertexLightStrength"
        };

        private static void ApplyFlatTone(Material material)
        {
            foreach (string property in ContrastFeatures.Where(material.HasProperty))
                material.SetFloat(property, 0f);

            // lilToon blends the scene's light color toward white. Keep a small amount of tint.
            if (material.HasProperty("_AsUnlit"))
                material.SetFloat("_AsUnlit", 0.85f);
            if (material.HasProperty("_LightMaxLimit"))
                material.SetFloat("_LightMaxLimit", 1f);
        }

        private sealed class Entry
        {
            public readonly string Name, Texture, TemplateRenderer;
            public readonly Color Color;

            public Entry(BinaryReader reader)
            {
                Name = reader.ReadString();
                Texture = reader.ReadString();
                TemplateRenderer = reader.ReadString();
                Color = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }
        }

        private readonly Dictionary<string, Entry> palette;
        private readonly Dictionary<string, string[]> rendererSlots;
        private readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>();

        public static NativeMaterialSet Load(AssetBundle bundle, GameObject prefab)
        {
            var manifest = bundle.LoadAsset<TextAsset>("NativeMaterialManifest");
            if (!manifest)
                throw new InvalidDataException("The assets bundle is missing NativeMaterialManifest.");
            using var reader = new BinaryReader(new MemoryStream(manifest.bytes));
            if (reader.ReadInt32() != 1)
                throw new InvalidDataException("Unsupported native material manifest version.");
            return new NativeMaterialSet(bundle, prefab, reader);
        }

        private NativeMaterialSet(AssetBundle bundle, GameObject prefab, BinaryReader reader)
        {
            palette = new Dictionary<string, Entry>();
            int materialCount = ReadCount(reader);
            for (int index = 0; index < materialCount; index++)
            {
                var entry = new Entry(reader);
                palette.Add(entry.Name, entry);
            }

            rendererSlots = new Dictionary<string, string[]>();
            int rendererCount = ReadCount(reader);
            for (int index = 0; index < rendererCount; index++)
            {
                string name = reader.ReadString();
                var slots = new string[ReadCount(reader)];
                for (int slot = 0; slot < slots.Length; slot++)
                    slots[slot] = reader.ReadString();
                rendererSlots.Add(name, slots);
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Unexpected data in native material manifest.");

            var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>();
            if (renderers.Length == 0 || renderers.Length != rendererSlots.Count)
                throw new InvalidDataException("The manifest does not cover every custom renderer.");

            foreach (var renderer in renderers)
            {
                if (!renderer.sharedMesh || !rendererSlots.TryGetValue(renderer.name, out var slots) ||
                    slots.Length != renderer.sharedMesh.subMeshCount || slots.Any(name => !palette.ContainsKey(name)))
                    throw new InvalidDataException("Invalid native material slots for " + renderer.name);
            }

            foreach (var entry in palette.Values)
            {
                Texture texture = Texture2D.whiteTexture;
                if (!string.IsNullOrEmpty(entry.Texture))
                {
                    texture = bundle.LoadAsset<Texture2D>(entry.Texture);
                    if (!texture)
                        throw new InvalidDataException("Missing base texture " + entry.Texture);
                }
                textures.Add(entry.Name, texture);
            }
        }

        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 1 || count > 1024)
                throw new InvalidDataException("Invalid count in native material manifest.");
            return count;
        }

        public Dictionary<string, Material[]> CreateRendererMaterials(SkinnedMeshRenderer[] originals)
        {
            var templates = new Dictionary<string, Material>();
            foreach (var name in palette.Values.Select(entry => entry.TemplateRenderer).Distinct())
            {
                var template = originals.Where(renderer => renderer.name == name)
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .FirstOrDefault(material => material && material.shader && material.HasProperty("_MainTex"));
                if (!template)
                    throw new InvalidDataException("Original character material was not found on " + name);
                templates.Add(name, template);
            }

            var copies = new Dictionary<string, Material>();
            try
            {
                foreach (var entry in palette.Values)
                {
                    var original = templates[entry.TemplateRenderer];
                    var material = new Material(original) { name = "DeepSeek_" + entry.Name };
                    copies.Add(entry.Name, material);
                    foreach (string property in BaseMaps.Where(material.HasProperty))
                    {
                        material.SetTexture(property, textures[entry.Name]);
                        material.SetTextureScale(property, Vector2.one);
                        material.SetTextureOffset(property, Vector2.zero);
                    }
                    foreach (string property in BaseColors.Where(material.HasProperty))
                        material.SetColor(property, entry.Color);
                    foreach (string property in OriginalUvMasks.Where(material.HasProperty))
                        material.SetTexture(property, null);

                    if (material.HasProperty("_Cull"))
                        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    if (material.HasProperty("_BackfaceColor"))
                        material.SetColor("_BackfaceColor", new Color(0f, 0f, 0f, 0.4f));

                    ApplyFlatTone(material);
                    Utils.ModLogger.Info($"{material.name}: {original.name} / {material.shader.name} (flat tone)");
                }

                return rendererSlots.ToDictionary(pair => pair.Key, pair => pair.Value.Select(name => copies[name]).ToArray());
            }
            catch
            {
                foreach (var material in copies.Values)
                    UnityEngine.Object.Destroy(material);
                throw;
            }
        }
    }
}





