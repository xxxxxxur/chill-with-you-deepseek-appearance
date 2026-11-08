import bpy
import json
import struct
import sys
from array import array
from pathlib import Path

project = Path(sys.argv[sys.argv.index('--') + 1])
destination = project / 'Assets' / 'DeepSeek'
textures = destination / 'Textures'
geometry = destination / 'Source'
scene = bpy.context.scene
rig = bpy.data.objects['Character_Rig']
objects = [obj for obj in scene.objects if obj.type == 'MESH' and any(
    mod.type == 'ARMATURE' and mod.object == rig for mod in obj.modifiers)]
assert {'Face', 'Body', 'Hair'} <= {obj.name for obj in objects}
materials = {
    'body.001': 'Body',
    'dress.001': 'Skirt',
    'dress2.001': 'SkirtAccent',
    'dress2': 'DressBodice',
    'hair.002': 'Hair',
    '材质.002': 'Face',
    '材质.003': 'Face',
    'eye': 'Eye',
    'hair.003': 'BrowHair',
    '材质.004': 'Eyebrow',
    '材质.006': 'EyelidAccent',
    '配饰.001': 'Accessories',
}

def render_uv(mesh):
    return next(uv for uv in mesh.uv_layers if uv.active_render)

def source_color(material, original_name=None):
    name = original_name or material.name
    tree = material.node_tree
    if name in {'dress.001', 'dress2.001'}:
        for node in tree.nodes:
            if node.type == 'BSDF_PRINCIPLED' and node.inputs['Base Color'].is_linked:
                return node.inputs['Base Color'].links[0].from_socket
    if name in {'body.001','材质.002'}:
        for node in tree.nodes:
            if node.type == 'BSDF_PRINCIPLED' and node.inputs['Base Color'].is_linked:
                return node.inputs['Base Color'].links[0].from_socket
    if name == 'hair.003':
        return tree.nodes['RGB.005'].outputs['Color']
    output = next(n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
    return output.inputs['Surface'].links[0].from_socket

def bake_color(material, name, size):
    vertices, triangles, uvs, colors = [], [], [], []
    bake_material = material.copy()
    color = source_color(bake_material, material.name)
    has_color = color.node.type == 'VERTEX_COLOR'
    for obj in objects:
        mesh = obj.data
        slots = {i for i, slot in enumerate(mesh.materials) if slot and materials.get(slot.name) == name}
        if not slots:
            continue
        uv = render_uv(mesh)
        mesh.calc_loop_triangles()
        attr = mesh.color_attributes.active_color if has_color else None
        if has_color:
            assert attr and attr.domain == 'CORNER'
        for triangle in mesh.loop_triangles:
            if triangle.material_index not in slots:
                continue
            triangles.append(tuple(range(len(vertices),len(vertices)+3)))
            for vi,li in zip(triangle.vertices,triangle.loops):
                vertices.append(obj.matrix_world @ mesh.vertices[vi].co)
                uvs.append(tuple(uv.data[li].uv))
                if attr:
                    colors.append(tuple(attr.data[li].color))
    assert triangles
    mesh = bpy.data.meshes.new('Bake geometry')
    mesh.from_pydata(vertices, [], triangles)
    mesh.update()
    uv = mesh.uv_layers.new(name='AlbedoUV')
    uv.data.foreach_set('uv',[v for pair in uvs for v in pair])
    if has_color:
        attr = mesh.color_attributes.new(name='AlbedoColor',type='FLOAT_COLOR',domain='CORNER')
        attr.data.foreach_set('color',[v for color_tuple in colors for v in color_tuple])
        color.node.layer_name = attr.name
    obj = bpy.data.objects.new('Bake geometry',mesh)
    scene.collection.objects.link(obj)
    mesh.materials.append(bake_material)
    tree = bake_material.node_tree
    output = next(n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
    emission = tree.nodes.new('ShaderNodeEmission')
    tree.links.new(color,emission.inputs['Color'])
    tree.links.new(emission.outputs['Emission'],output.inputs['Surface'])
    image = bpy.data.images.new(name,size,size,alpha=False)
    image.colorspace_settings.name='sRGB'
    node = tree.nodes.new('ShaderNodeTexImage')
    node.image = image
    tree.nodes.active = node
    for other in bpy.context.selected_objects:
        other.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 1
    scene.render.bake.margin = 32
    scene.render.bake.margin_type = 'EXTEND'
    scene.render.bake.use_selected_to_active = False
    bpy.ops.object.bake(type='EMIT')
    filename = name + '.png'
    image.filepath_raw = str(textures / filename)
    image.file_format = 'PNG'
    image.save()
    bpy.data.objects.remove(obj,do_unlink=True)
    bpy.data.meshes.remove(mesh)
    bpy.data.materials.remove(bake_material)
    return filename

material_info=[]
used = {o.data.materials[p.material_index].name for o in objects for p in o.data.polygons}
assert used <= set(materials), used-set(materials)
exported_materials = set()
for original,name in materials.items():
    if original not in used or name in exported_materials:
        continue
    exported_materials.add(name)
    material = bpy.data.materials[original]
    output = source_color(material)
    item = {'name':name,'blenderMaterial':original,'linearColor':[1,1,1,1]}
    if output.node.type == 'TEX_IMAGE':
        item['file'] = bake_color(material, name, max(output.node.image.size))
    elif output.node.type == 'RGB':
        item['linearColor'] = list(output.default_value)
    elif output.node.type in {'MIX','VERTEX_COLOR'}:
        item['file'] = bake_color(material,name,1024 if output.node.type == 'VERTEX_COLOR' else 2048)
    else:
        raise ValueError(f'Unsupported material output: {original} {output.node.type}')
    material_info.append(item)
    print('MATERIAL',item,flush=True)

def unity(vector):
    return (-vector[0],vector[2],-vector[1])

def write_string(file, value):
    encoded=value.encode('utf-8')
    file.write(struct.pack('<I',len(encoded)))
    file.write(encoded)

def write_int(file,value):
    file.write(struct.pack('<I',value))

def evaluate_skin(obj):
    mesh = obj.data
    blocks = list(mesh.shape_keys.key_blocks) if mesh.shape_keys else []
    keys = blocks[1:]
    assert all(key.relative_key == blocks[0] for key in keys), obj.name
    saved_values = [key.value for key in keys]

    def snapshot(capture_normals):
        bpy.context.view_layer.update()
        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        deformed = evaluated.data
        assert len(deformed.vertices) == len(mesh.vertices) and len(deformed.loops) == len(mesh.loops), obj.name
        world = evaluated.matrix_world
        positions = [world @ vertex.co for vertex in deformed.vertices]
        if not capture_normals:
            return positions
        assert all(a.vertex_index == b.vertex_index for a, b in zip(mesh.loops, deformed.loops)), obj.name
        normal_matrix = world.to_3x3().inverted().transposed()
        normals = [(normal_matrix @ corner.vector).normalized() for corner in deformed.corner_normals]
        return positions, normals

    try:
        for key in keys:
            key.value = 0.0
        base, normals = snapshot(True)
        shapes = []
        for key in keys:
            key.value = 1.0
            posed = snapshot(False)
            shapes.append((key.name, [unity(position - original) for position, original in zip(posed, base)]))
            key.value = 0.0
        return base, normals, shapes
    finally:
        for key, value in zip(keys, saved_values):
            key.value = value
        bpy.context.view_layer.update()

overlay_submeshes = []

def export_mesh(obj):
    mesh = obj.data
    mesh.calc_loop_triangles()
    positions, normals, shapes = evaluate_skin(obj)
    uv = render_uv(mesh)
    output_vertices = {}
    split = []
    submeshes = {}
    skin_groups = [g for g in obj.vertex_groups if g.name in rig.data.bones]
    indices = [g.name for g in skin_groups]
    skin_indices = {g.index:i for i,g in enumerate(skin_groups)}
    overlay_groups = []
    if obj.name == 'Face':
        for name in ('eyebrow', 'eye'):
            group = obj.vertex_groups.get(name)
            if group:
                overlay_groups.append({v.index for v in mesh.vertices
                                       if any(w.group == group.index and w.weight > 0 for w in v.groups)})
    weights = {}
    for vi in {index for tri in mesh.loop_triangles for index in tri.vertices}:
        groups=sorted((g for g in mesh.vertices[vi].groups if g.group in skin_indices and g.weight>0),
                      key=lambda g:g.weight,reverse=True)[:4]
        assert groups,(obj.name,vi)
        total=sum(g.weight for g in groups)
        weights[vi]=([skin_indices[g.group] for g in groups]+[0]*(4-len(groups)),
                     [g.weight/total for g in groups]+[0.0]*(4-len(groups)))
    with (geometry / (obj.name + '.bytes')).open('wb') as file:
        write_int(file,1)
        write_int(file,len(indices))
        for name in indices:
            write_string(file,name)
        for tri in mesh.loop_triangles:
            material = materials[mesh.materials[tri.material_index].name]
            is_overlay = any(all(index in vertices for index in tri.vertices) for vertices in overlay_groups)
            out = submeshes.setdefault((material,is_overlay),array('I'))
            # The headband was authored with its visible side facing inward.
            headband = obj.name == 'Hairband'
            loops = zip(tri.vertices,tri.loops) if headband else zip(reversed(tri.vertices),reversed(tri.loops))
            for vi,li in loops:
                surface_normal = -normals[li] if headband else normals[li]
                point = (vi,unity(surface_normal),tuple(uv.data[li].uv))
                idx = output_vertices.setdefault(point,len(output_vertices))
                if idx == len(split):
                    split.append(point)
                out.append(idx)
        write_int(file,len(split))
        for vi,n,t in split:
            ids,amounts = weights[vi]
            file.write(struct.pack('<8f4i4f',*unity(positions[vi]),*n,*t,*ids,*amounts))
        write_int(file,len(submeshes))
        for slot, ((name, is_overlay), triangles) in enumerate(submeshes.items()):
            if is_overlay:
                overlay_submeshes.append(slot)
                print('FACE_OVERLAY', slot, name, len(triangles) // 3, flush=True)
            write_string(file,name)
            write_int(file,len(triangles))
            triangles.tofile(file)
        write_int(file,len(shapes))
        for key_name, offsets in shapes:
            write_string(file,key_name)
            array('f',(c for vi,_,_ in split for c in offsets[vi])).tofile(file)
    print('MESH',obj.name,len(split),len(shapes),flush=True)

for obj in objects:
    export_mesh(obj)
face_keys = list(bpy.data.objects['Face'].data.shape_keys.key_blocks.keys())[1:]
reference_shapes = json.loads((project.parent/'game-character-extract'/'original_bind_pose.json').read_text(encoding='utf-8'))
assert face_keys == next(m['blend_shapes'] for m in reference_shapes['meshes'] if m['name'] == 'Face')
(geometry/'materials.json').write_text(json.dumps({
    'materials':material_info,'meshes':[o.name for o in objects],'faceBlendShapes':face_keys,
    'faceOverlaySubmeshes':overlay_submeshes
},ensure_ascii=False,indent=2),encoding='utf-8')

skeleton = json.loads((project.parent/'game-character-extract'/'original_bind_pose.json').read_text(encoding='utf-8'))
bones = [{'name':b['name'],'parent':b['parent'] or '', 'bind':[v for row in b['bind_matrix'] for v in row]} for b in skeleton['bones']]
(geometry/'skeleton.json').write_text(json.dumps({'bones':bones},indent=2),encoding='utf-8')







