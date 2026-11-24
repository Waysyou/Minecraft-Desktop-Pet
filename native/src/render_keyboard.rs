// Solid keyboard chassis and independent keycaps, driven by the original input state.
use super::*;
fn solid(size:Vec3, transform:Mat4, color:[u8;3], key:u32)->BoxPart {
    BoxPart {size,uv:Vec2::new(key as f32,0.),transform,inflate:0.,mirror:false,
        keyboard:true,color:Some(color),cape:false,faces:None,eyes:None,eyelid:1.,pupil:Vec2::ZERO}
}
pub(super) fn parts(pose:&Pose,height:f32)->Vec<BoxPart> {
    // Turn the entire physical board toward its owner, including legends and feet.
    let base=Mat4::from_translation(Vec3::new(0.,height,9.4))*Mat4::from_scale(Vec3::splat(pose.sit))
        *Mat4::from_rotation_y(std::f32::consts::PI);
    let mut boxes=Vec::with_capacity(90);
    for (size,offset,color) in [
        (Vec3::new(20.,0.48,8.25),Vec3::new(0.,-0.43,0.),[36,46,57]),
        (Vec3::new(19.7,0.30,7.98),Vec3::new(0.,-0.10,0.),[66,82,92]),
        (Vec3::new(18.75,0.10,7.32),Vec3::new(0.,0.08,-0.02),[28,36,44]),
        (Vec3::new(1.65,0.22,1.20),Vec3::new(-7.8,-0.72,-2.6),[23,29,34]),
        (Vec3::new(1.65,0.22,1.20),Vec3::new(7.8,-0.72,-2.6),[23,29,34]),
    ] {let mut part=solid(size,base*Mat4::from_translation(offset),color,0);part.uv.y=boxes.len() as f32;boxes.push(part);}
    for (r,row) in keyboard_rows().iter().enumerate() {
        let total:f32=row.iter().map(|k|k.1).sum();let mut x=-9.1;
        for &(vk,units) in row {
            let width=18.2*units/total;
            let pressed=pose.held.iter().any(|key|key.vk==vk);
            let mut color=match vk {
                27=>[230,158,137],
                13|32=>[154,193,167],
                65|83|68|87=>[187,211,201],
                8|9|20|160..=165|91..=93=>[166,181,189],
                _=>[229,231,220],
            };
            if pressed {color=[118,194,168];}
            let center=Vec3::new(x+width/2.,0.30-if pressed {0.19}else{0.},-3.1+r as f32*1.23);
            boxes.push(solid(Vec3::new((width-0.15).max(0.5),0.40,1.04),base*Mat4::from_translation(center),color,vk));
            x+=width;
        }
    }
    boxes
}
pub(super) fn legend(vk:u32)-> &'static str {
    match vk {
        65..=90=>&"ABCDEFGHIJKLMNOPQRSTUVWXYZ"[(vk-65) as usize..(vk-64) as usize],
        48..=57=>&"0123456789"[(vk-48) as usize..(vk-47) as usize],
        112..=123=>["F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12"][(vk-112) as usize],
        27=>"Esc",8=>"Bksp",9=>"Tab",20=>"Caps",13=>"Enter",32=>"Space",
        160|161=>"Shift",162|163=>"Ctrl",164|165=>"Alt",91|92=>"Win",93=>"Fn",
        186=>";",187=>"=",188=>",",189=>"-",190=>".",191=>"/",192=>"`",
        219=>"[",220=>"\\",221=>"]",222=>"'",_=>"",
    }
}
// Project legends onto the true top faces. This shares the original cached font.
pub(super) fn legend_alpha(text:&str,u:f32,v:f32)->f32 {
    if text.is_empty() || !(0.13..0.87).contains(&u) || !(0.16..0.84).contains(&v){return 0.;}
    let len=text.len() as f32;
    let span=if len==1. {0.60}else{0.74};let left=(1.-span)/2.;
    if u<left || u>=left+span{return 0.;}
    let t=(u-left)/span*len;let index=(t as usize).min(text.len()-1);
    let ch=text.as_bytes()[index];if !(32..128).contains(&ch){return 0.;}
    let x=((t.fract()*24.) as usize).min(23);let y=(((v-0.16)/0.68*24.) as usize).min(23);
    effects::glyphs()[(ch-32) as usize][y*24+x] as f32/255.
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn caps_follow_layout_and_real_key_state() {
        let mut s=Settings::default();s.posture="sitting".into();let mut p=Pose::new(&s);p.sit=1.;
        let rest=parts(&p,10.2);assert_eq!(rest.iter().filter(|b|b.uv.x>0.).count(),74);
        let a=rest.iter().find(|b|b.uv.x==65.).unwrap();let z=rest.iter().find(|b|b.uv.x==90.).unwrap();
        assert!(a.transform.w_axis.x>0. && z.transform.w_axis.z<a.transform.w_axis.z);
        let y=a.transform.w_axis.y;p.key(65,true,1.,&s);
        let down=parts(&p,10.2);let a=down.iter().find(|b|b.uv.x==65.).unwrap();assert!(y-a.transform.w_axis.y>0.18);
        p.key(65,false,1.1,&s);let up=parts(&p,10.2);assert!((up.iter().find(|b|b.uv.x==65.).unwrap().transform.w_axis.y-y).abs()<0.001);
    }
    #[test]
    fn legends_have_correct_names_and_opaque_ink() {
        assert_eq!(legend(162),"Ctrl");assert_eq!(legend(65),"A");assert_eq!(legend(112),"F1");
        let ink=(0..40).flat_map(|x|(0..40).map(move|y|(x,y))).filter(|&(x,y)|legend_alpha("A",x as f32/40.,y as f32/40.)>0.5).count();assert!(ink>4);
    }
    #[test]
    fn actor_layout_turns_both_axes_and_all_caps_match_input() {
        assert!(key_position(27).unwrap().x>0. && key_position(123).unwrap().x<0.);
        assert!(key_position(27).unwrap().y>key_position(32).unwrap().y);
        assert!(key_position(162).unwrap().x>key_position(163).unwrap().x);
        for height in [10.2,5.] {
            let s=Settings::default();let mut p=Pose::new(&s);p.sit=1.;
            for cap in parts(&p,height).iter().filter(|b|b.uv.x>0.) {
                let k=key_position(cap.uv.x as u32).unwrap();
                let center=cap.transform.transform_point3(Vec3::ZERO);
                assert!((center.x-k.x).abs()<0.00001 && (center.z-9.4-k.y).abs()<0.00001);
            }
        }
    }
    #[test]
    fn precise_hands_and_letters_follow_actor_coordinates() {
        for chibi in [false,true] {
            let mut s=Settings::default();s.posture="sitting".into();s.typing_mode="precise".into();s.typing_letters=true;s.chibi=chibi;
            let mut p=Pose::new(&s);
            p.key(81,true,1.,&s);assert_eq!(p.held.last().unwrap().side,0);
            assert_eq!(p.letters.last().unwrap().pos,key_position(81).unwrap());
            p.key(80,true,1.1,&s);assert_eq!(p.held.last().unwrap().side,1);
            assert_eq!(p.letters.last().unwrap().pos,key_position(80).unwrap());
        }
    }
    #[test]
    #[ignore]
    fn actor_keyboard_preview_png() {
        let out=std::path::PathBuf::from(r"G:\Desktop-Apps\codex-integration\qa-native\actor-keyboard");
        let profile=std::path::PathBuf::from(r"G:\Desktop-Apps\data\Minecraft-Desktop-Pet");
        std::fs::create_dir_all(&out).unwrap();
        let mut s:Settings=serde_json::from_slice(&std::fs::read(profile.join("settings.json")).unwrap()).unwrap();
        s.track_mouse=false;s.typing_squash=0.;s.da_dance=false;s.breathing_animation=false;s.typing_mode="precise".into();
        let skin=Skin::load(&std::fs::read(profile.join("skin.png")).unwrap()).unwrap();
        let mut proof=Vec::new();
        for (name,vk) in [("idle",0),("Esc",27),("F12",123),("Q",81),("P",80),("Space",32)] {
            let mut p=Pose::new(&s);
            for i in 0..120 {p.update(&s,&skin,Vec2::ZERO,720.,840.,false,i as f32/60.,1./60.);}
            if vk!=0 {p.key(vk,true,2.,&s);for i in 0..30 {p.update(&s,&skin,Vec2::ZERO,720.,840.,false,2.+i as f32/120.,1./120.);}}
            let mut f=Frame::new(720,840);f.draw(&s,&skin,&p);
            image::save_buffer(out.join(format!("actor-{name}.png")),&f.rgba(),720,840,image::ColorType::Rgba8).unwrap();
            if let Some(k)=key_position(vk) {
                proof.push(serde_json::json!({"key":name,"modelX":k.x,"modelZOffset":k.y,"hand":if k.x>0. {"actor-left"}else{"actor-right"}}));
            }
        }
        std::fs::write(out.join("actor-orientation.json"),serde_json::to_vec_pretty(&proof).unwrap()).unwrap();
    }
    #[test]
    #[ignore]
    fn keyboard_preview_png() {
        let dir=std::path::PathBuf::from(std::env::var("PET_KEYBOARD_SHOTS").unwrap());std::fs::create_dir_all(&dir).unwrap();
        let profile=std::path::PathBuf::from(r"G:\Desktop-Apps\data\Minecraft-Desktop-Pet");
        let mut s:Settings=serde_json::from_slice(&std::fs::read(profile.join("settings.json")).unwrap()).unwrap();
        s.track_mouse=false;s.typing_squash=0.;s.da_dance=false;s.breathing_animation=false;
        let skin=Skin::load(&std::fs::read(profile.join("skin.png")).unwrap()).unwrap();
        let mut p=Pose::new(&s);
        for i in 0..180 {p.update(&s,&skin,Vec2::ZERO,720.,840.,false,i as f32/60.,1./60.);}
        let mut frame=Frame::new(720,840);frame.draw(&s,&skin,&p);
        image::save_buffer(dir.join("keyboard-idle.png"),&frame.rgba(),720,840,image::ColorType::Rgba8).unwrap();
        p.key(87,true,3.,&s);p.update(&s,&skin,Vec2::ZERO,720.,840.,false,3.1,0.1);
        frame.draw(&s,&skin,&p);image::save_buffer(dir.join("keyboard-W-down.png"),&frame.rgba(),720,840,image::ColorType::Rgba8).unwrap();
    }
}
