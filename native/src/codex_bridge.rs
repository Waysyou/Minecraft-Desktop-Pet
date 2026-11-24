//! Codex controls belong to the pet's existing tray menu; no second tray icon.
use std::{path::PathBuf,ptr::{null,null_mut}};
use windows_sys::Win32::UI::WindowsAndMessaging::*;
use crate::wide;
const CONTROL:&str="Waysyou.MinecraftCodexBridge.Control.v1";
fn executable()->PathBuf {PathBuf::from(r"G:\Desktop-Apps\codex-integration\MinecraftCodexBridge.exe")}
unsafe fn command(id:usize)->usize {
    let window=FindWindowW(null(),wide(CONTROL).as_ptr());if window.is_null(){return 0;}
    let mut result=0usize;
    SendMessageTimeoutW(window,RegisterWindowMessageW(wide(CONTROL).as_ptr()),id,0,SMTO_ABORTIFHUNG,150,&mut result);
    result
}
pub(super) unsafe fn state()->usize {command(0)}
pub(super) unsafe fn toggle(){command(1);}
pub(super) unsafe fn stop(){command(4);}
pub(super) unsafe fn appearance(){
    if state()&1!=0{command(5);return;}
    use std::os::windows::process::CommandExt;
    let exe=executable();if !exe.is_file(){return;}
    let _=std::process::Command::new(&exe).arg("--appearance").current_dir(exe.parent().unwrap()).creation_flags(0x08000000).spawn();
}
pub(super) unsafe fn key_stats(){
    if state()&1!=0{command(6);return;}
    use std::os::windows::process::CommandExt;
    let exe=executable();if !exe.is_file(){return;}
    let _=std::process::Command::new(&exe).arg("--key-stats").current_dir(exe.parent().unwrap()).creation_flags(0x08000000).spawn();
}
pub(super) unsafe fn start(){
    if state()&1!=0{return;}
    use std::os::windows::process::CommandExt;
    let exe=executable();if !exe.is_file(){return;}
    let _=std::process::Command::new(&exe).current_dir(exe.parent().unwrap()).creation_flags(0x08000000).spawn();
}
pub(super) unsafe fn append_to(menu:HMENU) {
    let status=state();let submenu=CreatePopupMenu();if submenu.is_null(){return;}
    AppendMenuW(submenu,MF_STRING | if status&1==0{MF_GRAYED}else{0} | if status&2!=0{MF_CHECKED}else{0},180,wide("显示任务与额度气泡").as_ptr());
    AppendMenuW(submenu,MF_SEPARATOR,0,null());
    AppendMenuW(submenu,MF_STRING,183,wide("气泡外观设置…").as_ptr());
    AppendMenuW(submenu,MF_STRING | if status&1!=0{MF_GRAYED}else{0},181,wide("启动 Codex 联动").as_ptr());
    AppendMenuW(submenu,MF_STRING | if status&1==0{MF_GRAYED}else{0},182,wide("停止联动（保留桌宠）").as_ptr());
    AppendMenuW(menu,MF_POPUP,submenu as usize,wide("Codex 任务与额度").as_ptr());
    AppendMenuW(menu,MF_SEPARATOR,0,null_mut());
}
