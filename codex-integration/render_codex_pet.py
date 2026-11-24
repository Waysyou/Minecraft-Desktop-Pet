"""Export the installed Minecraft skin as a native Codex V2 sprite atlas source.
Uses Blender in a separate background process; never edits an interactive scene.
Cuboid UV layout follows DLmember/Minecraft-Desktop-Pet render.rs (MIT).
"""
import bpy, math, json
from pathlib import Path
from mathutils import Matrix, Vector

ROOT = Path('G:/Desktop-Apps/codex-integration')
FRAMES = ROOT/'frames'
FRAMES.mkdir(parents=True, exist_ok=True)
DATA = Path('G:/Desktop-Apps/data/Minecraft-Desktop-Pet')
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = 'BLENDER_EEVEE'
scene.eevee.taa_render_samples = 16
scene.render.resolution_x = 384
scene.render.resolution_y = 416
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.render.film_transparent = True
scene.view_settings.view_transform = 'Standard'
scene.view_settings.look = 'None'
scene.view_settings.exposure = 0
scene.view_settings.gamma = 1
scene.render.image_settings.color_depth = '8'

# Minecraft uses Y-up and +Z-front; Blender uses Z-up.
C = Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
def t(x=0,y=0,z=0): return Matrix.Translation((x,y,z))
def rx(a): return Matrix.Rotation(a,4,'X')
def ry(a): return Matrix.Rotation(a,4,'Y')
def rz(a): return Matrix.Rotation(a,4,'Z')
def mc_matrix(obj,m): obj.matrix_world = C @ m

skin = bpy.data.images.load(str(DATA/'skin.png'), check_existing=True)
cape = bpy.data.images.load(str(DATA/'cape.png'), check_existing=True) if (DATA/'cape.png').exists() else None
materials = {}
def material(image,face,outer=False):
    key=(image.name,face,outer)
    if key in materials:return materials[key]
    m=bpy.data.materials.new('_'.join(map(str,key)));m.use_nodes=True
    n=m.node_tree.nodes;n.clear();links=m.node_tree.links
    out=n.new('ShaderNodeOutputMaterial');tex=n.new('ShaderNodeTexImage');tex.image=image;tex.interpolation='Closest'
    mult=n.new('ShaderNodeMixRGB');mult.blend_type='MULTIPLY';mult.inputs[0].default_value=1
    shade=[.8,.8,1,.62,.94,.86][face];mult.inputs[2].default_value=(shade,shade,shade,1)
    links.new(tex.outputs['Color'],mult.inputs[1])
    emission=n.new('ShaderNodeEmission');links.new(mult.outputs[0],emission.inputs['Color'])
    if outer:
        transparent=n.new('ShaderNodeBsdfTransparent');mix=n.new('ShaderNodeMixShader')
        links.new(tex.outputs['Alpha'],mix.inputs[0]);links.new(transparent.outputs[0],mix.inputs[1]);links.new(emission.outputs[0],mix.inputs[2]);links.new(mix.outputs[0],out.inputs['Surface'])
    else: links.new(emission.outputs[0],out.inputs['Surface'])
    materials[key]=m;return m

def cuboid(name,size,uv=(0,0),image=skin,inflate=0):
    w,h,d=size;u,v=uv;sz=(w+inflate,h+inflate,d+inflate)
    regions=[(u+d+w,v+d,d,h),(u,v+d,d,h),(u+d,v,w,d),(u+d+w,v,w,d),(u+d,v+d,w,h),(u+2*d+w,v+d,w,h)]
    planes=[(2,1,0,-1,-1,sz[2],sz[1],sz[0]),(2,1,0,1,-1,sz[2],sz[1],-sz[0]),(0,2,1,1,1,sz[0],sz[2],sz[1]),(0,2,1,1,-1,sz[0],sz[2],-sz[1]),(0,1,2,1,-1,sz[0],sz[1],sz[2]),(0,1,2,-1,-1,sz[0],sz[1],-sz[2])]
    vertices=[];faces=[];uvs=[]
    for face,(ua,va,wa,ud,vd,ww,hh,dd) in enumerate(planes):
        x,y,tw,th=regions[face];base=len(vertices)
        for iy,ix in [(0,0),(0,1),(1,1),(1,0)]:
            p=[0.,0.,0.];p[ua]=(ix*ww-ww/2)*ud;p[va]=(iy*hh-hh/2)*vd;p[wa]=dd/2
            vertices.append(p);uvs.append(((x+ix*tw)/64,1-(y+((1-iy) if face==3 else iy)*th)/(32 if image==cape else 64)))
        faces.append(tuple(range(base,base+4)))
    mesh=bpy.data.meshes.new(name);mesh.from_pydata(vertices,[],faces);mesh.update()
    layer=mesh.uv_layers.new()
    for poly in mesh.polygons:
        for li in poly.loop_indices:layer.data[li].uv=uvs[mesh.loops[li].vertex_index]
    obj=bpy.data.objects.new(name,mesh);scene.collection.objects.link(obj)
    for f in range(6):mesh.materials.append(material(image,f,inflate>0))
    for poly in mesh.polygons:poly.material_index=poly.index
    return obj

parts={}
def part(name,size,uv,outer):
    parts[name]=[cuboid(name,size,uv),cuboid(name+'_outer',size,outer,inflate=1 if name=='head' else .5)]
part('body',(8,12,4),(16,16),(16,32))
part('head',(8,8,8),(0,0),(32,0))
part('left_arm',(3,12,4),(32,48),(48,48))
part('right_arm',(3,12,4),(40,16),(40,32))
part('left_leg',(4,12,4),(16,48),(0,48))
part('right_leg',(4,12,4),(0,16),(0,32))
if cape:parts['cape']=[cuboid('cape',(10,16,1),(0,0),cape)]

# A small keyboard is a mesh prop, as in the original desktop pet.
keyboard=bpy.data.objects.new('keyboard',bpy.data.meshes.new('keyboard_mesh'))
bpy.ops.mesh.primitive_cube_add(size=1)
keyboard=bpy.context.object;keyboard.name='keyboard';keyboard.scale=(9,.9,3)
kbmat=bpy.data.materials.new('keyboard_gray');kbmat.diffuse_color=(.16,.19,.23,1);kbmat.use_nodes=True
pr=kbmat.node_tree.nodes.get('Principled BSDF');pr.inputs['Base Color'].default_value=(.16,.19,.23,1);pr.inputs['Roughness'].default_value=1
keyboard.data.materials.append(kbmat)
for row in range(3):
    for col in range(10):
        bpy.ops.mesh.primitive_cube_add(size=1);key=bpy.context.object;key.name=f'key_{row}_{col}'
        key.data.materials.append(kbmat)
keys=[o for o in scene.objects if o.name.startswith('key_')]

camdata=bpy.data.cameras.new('camera');camera=bpy.data.objects.new('camera',camdata);scene.collection.objects.link(camera)
camera.location=Vector((30,-70,39));target=Vector((0,0,17))
camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler()
camdata.type='ORTHO';camdata.ortho_scale=47;scene.camera=camera
scene.world=bpy.data.worlds.new('World');scene.world.color=(.6,.6,.6)
lightdata=bpy.data.lights.new('keylight','AREA');lightdata.energy=800;lightdata.shape='DISK';lightdata.size=60
light=bpy.data.objects.new('keylight',lightdata);scene.collection.objects.link(light);light.location=(0,-30,55)

BASE_YAW=math.atan2(30,70)
def pose(state,phase,direction=None):
    s=math.sin(phase*math.tau);c=math.cos(phase*math.tau)
    height=12;head_yaw=BASE_YAW;head_pitch=-.18;head_roll=0;body_yaw=0;body_pitch=0
    arms=[(-.035,0,.105),(-.035,0,-.105)];legs=[(0,0,0),(0,0,0)]
    keyboard.hide_render=state!='running'
    for key in keys:key.hide_render=keyboard.hide_render
    if state=='idle': height+=.12*s;head_yaw+=.025*s;arms[0]=(-.035,0,.105+.025*s);arms[1]=(-.035,0,-.105-.025*s)
    elif state in ('running-right','running-left'):
        body_yaw=BASE_YAW+(math.pi/2 if state.endswith('right') else -math.pi/2)
        head_yaw=0;height+=.5*abs(s)
        legs=[(.65*s,0,0),(-.65*s,0,0)];arms=[(-.6*s,0,.1),(.6*s,0,-.1)]
    elif state=='waving':arms[1]=(-.1,0,-2.45+.25*s);head_roll=.05*s
    elif state=='jumping':
        height+=[0,2.8,5,2.8,0][int(round(phase*4))];arms=[(0,0,.4),(0,0,-.4)];legs=[(.15*s,0,0),(-.15*s,0,0)]
    elif state=='failed':head_pitch=.32+.09*s;head_roll=.1;body_pitch=.08;arms[1]=(-.15,0,-.2)
    elif state=='waiting':head_yaw+=.20*s;head_roll=.035*c;arms[0]=(-.15,0,.18)
    elif state=='running':
        arms=[(-.90+.1*s,0,.08),(-.90-.1*s,0,-.08)];head_pitch=.15;body_pitch=.04
    elif state=='review':head_pitch=.18+.08*s;head_yaw+=.15;arms[1]=(-2.0,.15,-.15);head_roll=.04*s
    elif state=='look':
        head_yaw+=math.sin(direction)*.58;head_pitch=-.18-math.cos(direction)*.48
    root=t(0,height,0)@ry(body_yaw)
    torso=root@rx(body_pitch)
    transforms={'body':torso@t(0,6,0),'head':torso@t(0,12,0)@ry(head_yaw)@rx(head_pitch)@rz(head_roll)@t(0,4,0)}
    for i,(name,sign) in enumerate([('left',1),('right',-1)]):
        ax,ay,az=arms[i];lx,ly,lz=legs[i]
        transforms[name+'_arm']=torso@t(sign*5.5,10,0)@rx(ax)@ry(ay)@rz(az)@t(0,-4,0)
        transforms[name+'_leg']=root@t(sign*2,0,0)@rx(lx)@ry(ly)@rz(lz)@t(0,-6,0)
    if cape:transforms['cape']=torso@t(0,11,-2.6)@rx(.12+.025*s)@t(0,-8,-.5)
    for name,objects in parts.items():
        for obj in objects:mc_matrix(obj,transforms[name])
    # Keyboard uses Blender geometry dimensions independent of the arm meshes.
    keyboard.matrix_world=C@t(0,12,8.5)@Matrix.Diagonal((18,1.8,6,1))
    for key in keys:
        _,row,col=key.name.split('_');row=int(row);col=int(col)
        key.matrix_world=C@t(-7.2+col*1.6,13.05,6.7+row*1.8)@Matrix.Diagonal((1.3,.3,1.4,1))

rows=[('idle',6),('running-right',8),('running-left',8),('waving',4),('jumping',5),('failed',8),('waiting',6),('running',6),('review',6)]
manifest=[]
for row,(state,count) in enumerate(rows):
    for col in range(count):
        phase=col/(count-1) if state=='jumping' else col/count
        pose(state,phase);scene.render.filepath=str(FRAMES/f'{row:02}-{col:02}.png');bpy.ops.render.render(write_still=True)
        manifest.append(dict(row=row,col=col,state=state,path=scene.render.filepath))
pose('idle',0);scene.render.filepath=str(FRAMES/'00-06.png');bpy.ops.render.render(write_still=True)
manifest.append(dict(row=0,col=6,state='neutral',path=scene.render.filepath))
for i in range(16):
    pose('look',0,i*math.tau/16);row=9+i//8;col=i%8;scene.render.filepath=str(FRAMES/f'{row:02}-{col:02}.png');bpy.ops.render.render(write_still=True)
    manifest.append(dict(row=row,col=col,state=f'look-{i*22.5:05.1f}',path=scene.render.filepath))
(ROOT/'frames.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('PET_EXPORT_COMPLETE',len(manifest),flush=True)
