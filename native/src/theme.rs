use super::*;
use windows_sys::Win32::System::Registry::*;

const fn rgb(r: u32, g: u32, b: u32) -> u32 {
    r | g << 8 | b << 16
}

struct Palette {
    paper: u32,
    card: u32,
    field: u32,
    ink: u32,
    muted: u32,
    edge: u32,
    hover: u32,
    accent: u32,
    on_accent: u32,
}

fn palette(dark: bool) -> Palette {
    if dark {
        Palette {
            paper: rgb(24, 33, 29), card: rgb(35, 47, 41), field: rgb(44, 58, 50),
            ink: rgb(235, 241, 230), muted: rgb(168, 188, 174), edge: rgb(65, 84, 72),
            hover: rgb(54, 75, 62), accent: rgb(160, 205, 170), on_accent: rgb(22, 51, 37),
        }
    } else {
        Palette {
            paper: rgb(241, 240, 233), card: rgb(252, 251, 246), field: rgb(255, 255, 252),
            ink: rgb(27, 46, 39), muted: rgb(77, 93, 84), edge: rgb(215, 220, 208),
            hover: rgb(229, 238, 226), accent: rgb(28, 73, 53), on_accent: rgb(250, 251, 241),
        }
    }
}

pub(super) unsafe fn system_dark()->bool {
    let mut value=1u32;let mut len=4u32;
    RegGetValueW(HKEY_CURRENT_USER,wide("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize").as_ptr(),wide("AppsUseLightTheme").as_ptr(),RRF_RT_REG_DWORD,null_mut(),&mut value as *mut _ as *mut _,&mut len)==0 && value==0
}
unsafe fn fill(dc:HDC,rect:&RECT,color:u32) {
    SetDCBrushColor(dc,color);FillRect(dc,rect,GetStockObject(DC_BRUSH) as HBRUSH);
}

unsafe fn rounded(dc: HDC, rect: &RECT, color: u32, edge: u32, diameter: i32) {
    SetDCBrushColor(dc, color);
    SetDCPenColor(dc, edge);
    let old_brush = SelectObject(dc, GetStockObject(DC_BRUSH));
    let old_pen = SelectObject(dc, GetStockObject(DC_PEN));
    RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, diameter, diameter);
    SelectObject(dc, old_pen);
    SelectObject(dc, old_brush);
}

fn rect(scale: f32, left: i32, top: i32, right: i32, bottom: i32) -> RECT {
    let px = |value| (value as f32 * scale).round() as i32;
    RECT { left: px(left), top: px(top), right: px(right), bottom: px(bottom) }
}

fn sections(page: u8) -> &'static [[i32; 4]] {
    match page {
        0 => &[[20, 122, 740, 323], [20, 334, 740, 548], [20, 559, 740, 769]],
        1 => &[[20, 122, 740, 307], [20, 316, 740, 641]],
        2 => &[[20, 122, 740, 202], [20, 211, 740, 674]],
        _ => &[[20, 122, 740, 266], [20, 276, 740, 394]],
    }
}

unsafe fn label(dc: HDC, value: &str, area: &mut RECT, color: u32) {
    SetBkMode(dc, TRANSPARENT as i32);
    SetTextColor(dc, color);
    DrawTextW(dc, wide(value).as_ptr(), -1, area, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
}

impl App {
    pub(super) unsafe fn apply_theme(&mut self) {
        self.panel_dark=self.theme_override.unwrap_or_else(||system_dark());
        if self.panel.is_null(){return;}
        let dark=self.panel_dark as i32;
        windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute(self.panel,20,&dark as *const _ as *const _,4);
        self.github_icon();
        RedrawWindow(self.panel,null(),null_mut(),RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN);
    }
    pub(super) unsafe fn theme_message(&mut self,hwnd:HWND,msg:u32,w:WPARAM,l:LPARAM)->Option<LRESULT> {
        if msg==WM_SETTINGCHANGE || msg==WM_THEMECHANGED {self.apply_theme();return Some(0);}
        let colors = palette(self.panel_dark);
        match msg {
            WM_ERASEBKGND => { self.draw_console_background(hwnd, w as HDC, &colors); Some(1) }
            WM_CTLCOLORSTATIC | WM_CTLCOLORBTN | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX=>{
                let dc=w as HDC;
                let child=l as HWND;
                let is_field=msg==WM_CTLCOLOREDIT || msg==WM_CTLCOLORLISTBOX;
                let color=if is_field {colors.field} else {self.console_control_background(child,&colors)};
                let id=GetDlgCtrlID(child);
                let footer=self.layout.iter().any(|&(control, [_,y,_,_])| control==child && y>=807);
                let muted=IsWindowEnabled(child)==0 || footer || id==332 || (id==333 && self.online_notice.is_empty());
                SetTextColor(dc,if muted {colors.muted}else{colors.ink});
                SetBkColor(dc,color);
                SetBkMode(dc,if is_field {OPAQUE as i32}else{TRANSPARENT as i32});
                SetDCBrushColor(dc,color);Some(GetStockObject(DC_BRUSH) as isize)
            }
            WM_NOTIFY=>self.console_control_draw(l,&colors),
            _=>None,
        }
    }
    fn console_control_background(&self, hwnd: HWND, colors: &Palette) -> u32 {
        let y=self.layout.iter().find(|&&(control,_)|control==hwnd).map(|&(_,bounds)|bounds[1]);
        if y.map_or(false,|top| sections(self.panel_page).iter().any(|section|top>=section[1] && top<section[3])) {
            colors.card
        } else {
            colors.paper
        }
    }

    unsafe fn draw_console_background(&self, hwnd: HWND, dc: HDC, colors: &Palette) {
        let mut client: RECT=std::mem::zeroed();
        GetClientRect(hwnd,&mut client);
        fill(dc,&client,colors.paper);
        let scale=self.panel_scale;
        let header=rgb(25,53,43);
        let gold=rgb(229,211,149);
        rounded(dc,&rect(scale,20,12,740,63),header,header,(16.*scale) as i32);
        fill(dc,&rect(scale,34,29,40,35),gold);
        fill(dc,&rect(scale,40,35,46,41),rgb(156,188,153));
        fill(dc,&rect(scale,34,41,40,47),gold);
        let heading_font=CreateFontW(
            (-21.*scale) as i32,0,0,0,650,0,0,0,DEFAULT_CHARSET as u32,0,0,
            CLEARTYPE_QUALITY as u32,0,wide("Microsoft YaHei UI").as_ptr(),
        );
        let old_font=SelectObject(dc,heading_font);
        label(dc,"桌宠控制台",&mut rect(scale,57,15,379,60),rgb(250,251,241));
        SelectObject(dc,self.font);
        let context=["皮肤与桌面互动","视线跟随与整体角度","调整站姿与坐姿","键盘与鼠标用量"];
        label(dc,context[self.panel_page as usize],&mut rect(scale,516,19,722,56),rgb(202,221,204));
        SelectObject(dc,old_font);
        DeleteObject(heading_font);
        for &[left,top,right,bottom] in sections(self.panel_page) {
            rounded(dc,&rect(scale,left,top,right,bottom),colors.card,colors.edge,(16.*scale) as i32);
        }
        if self.panel_page==0 {
            let old_font=SelectObject(dc,self.font);
            for (y,title) in [(126,"皮肤与姿势"),(339,"桌面与互动"),(565,"尺寸与性能")] {
                fill(dc,&rect(scale,34,y+7,37,y+19),colors.accent);
                label(dc,title,&mut rect(scale,44,y,704,y+24),colors.accent);
            }
            SelectObject(dc,old_font);
        }
        if self.panel_page==2 {
            for y in [258,312,366,420,474,528,582] {
                fill(dc,&rect(scale,34,y,726,y+1),colors.edge);
            }
        }
        fill(dc,&rect(scale,20,774,740,775),colors.edge);
    }

    unsafe fn console_control_draw(&self,l:LPARAM,colors:&Palette)->Option<LRESULT> {
        if l==0{return None;}
        let hdr=&*(l as *const NMHDR);
        if hdr.code!=NM_CUSTOMDRAW {return None;}
        let draw=&*(l as *const NMCUSTOMDRAW);
        let dc=draw.hdc;let hwnd=hdr.hwndFrom;
        let mut class=[0u16;64];let n=GetClassNameW(hwnd,class.as_mut_ptr(),64);
        let class=String::from_utf16_lossy(&class[..n as usize]);
        if class=="msctls_trackbar32" {
            if draw.dwDrawStage!=CDDS_PREPAINT { return None; }
            fill(dc,&draw.rc,self.console_control_background(hwnd,colors));
            let mut channel:RECT=std::mem::zeroed();
            let mut thumb=channel;
            SendMessageW(hwnd,TBM_GETCHANNELRECT,0,&mut channel as *mut _ as isize);
            SendMessageW(hwnd,TBM_GETTHUMBRECT,0,&mut thumb as *mut _ as isize);
            let enabled=IsWindowEnabled(hwnd)!=0;
            let center=(thumb.top+thumb.bottom)/2;
            let radius=(6.*self.panel_scale).round().max(3.) as i32;
            let thickness=(2.*self.panel_scale).round().max(1.) as i32;
            let left=channel.left;
            let right=channel.right;
            let point=(thumb.left+thumb.right)/2;
            let bar=RECT{left,top:center-thickness,right,bottom:center+thickness};
            fill(dc,&bar,colors.edge);
            fill(dc,&RECT{right:point.clamp(left,right.max(left)),..bar},if enabled {colors.accent}else{colors.muted});
            let knob=RECT{left:point-radius,right:point+radius+1,top:center-radius,bottom:center+radius+1};
            rounded(dc,&knob,if enabled {colors.accent}else{colors.edge},if enabled {colors.accent}else{colors.muted},radius*2);
            if GetFocus()==hwnd {
                let mut focus=draw.rc;
                InflateRect(&mut focus,-2,-2);
                DrawFocusRect(dc,&focus);
            }
            return Some(CDRF_SKIPDEFAULT as isize);
        }
        if class!="Button" || draw.dwDrawStage!=CDDS_PREPAINT {return None;}
        let style=GetWindowLongPtrW(hwnd,GWL_STYLE) as u32;
        let ty=style & BS_TYPEMASK as u32;
        let check=ty==BS_CHECKBOX as u32 || ty==BS_RADIOBUTTON as u32;
        let is_tab=(150..=153).contains(&hdr.idFrom);
        let checked=if is_tab {hdr.idFrom==150+self.panel_page as usize}
            else {SendMessageW(hwnd,BM_GETCHECK,0,0)==BST_CHECKED as isize};
        let enabled=IsWindowEnabled(hwnd)!=0;
        let push=!check || style&BS_PUSHLIKE as u32!=0;
        let mut rc=draw.rc;
        let back=self.console_control_background(hwnd,colors);
        fill(dc,&rc,back);
        let primary=enabled && push && (checked || matches!(hdr.idFrom, 130 | 190));
        let fg=if !enabled {colors.muted} else if primary {colors.on_accent} else {colors.ink};
        if push {
            let hot=draw.uItemState&(CDIS_HOT|CDIS_SELECTED)!=0;
            let bg=if primary {colors.accent}else if hot {colors.hover}else{colors.field};
            rounded(dc,&rc,bg,if primary || hot {colors.accent}else{colors.edge},(10.*self.panel_scale) as i32);
            if checked && is_tab {
                let offset=(8.*self.panel_scale) as i32;
                fill(dc,&RECT{left:rc.left+offset,top:rc.top+offset,right:rc.left+offset+2,bottom:rc.bottom-offset},colors.on_accent);
            }
        } else {
            let size=(13.*self.panel_scale).round() as i32;
            let top=rc.top+(rc.bottom-rc.top-size)/2;
            let mark=RECT{left:rc.left+2,top,right:rc.left+2+size,bottom:top+size};
            let pen=CreatePen(PS_SOLID,1,if checked {colors.accent}else{colors.muted});
            let oldpen=SelectObject(dc,pen);SetDCBrushColor(dc,if checked {colors.accent}else{colors.field});let oldbrush=SelectObject(dc,GetStockObject(DC_BRUSH));
            if ty==BS_RADIOBUTTON as u32 {Ellipse(dc,mark.left,mark.top,mark.right,mark.bottom);} else {RoundRect(dc,mark.left,mark.top,mark.right,mark.bottom,4,4);}
            SelectObject(dc,oldbrush);SelectObject(dc,oldpen);DeleteObject(pen);
            if checked {
                let pad=(size as f32*0.30) as i32;
                let inner=RECT{left:mark.left+pad,top:mark.top+pad,right:mark.right-pad,bottom:mark.bottom-pad};
                if ty==BS_RADIOBUTTON as u32 {fill(dc,&inner,colors.on_accent);} else {
                    let pen=CreatePen(PS_SOLID,(2.*self.panel_scale).round() as i32,colors.on_accent);let old=SelectObject(dc,pen);
                    MoveToEx(dc,mark.left+size/5,top+size/2,null_mut());LineTo(dc,mark.left+size*2/5,top+size*3/4);LineTo(dc,mark.left+size*4/5,top+size/4);SelectObject(dc,old);DeleteObject(pen);
                }
            }
            rc.left=mark.right+(5.*self.panel_scale) as i32;
        }
        if hdr.idFrom==169 && self.github_images!=0 {
            let size=(18.*self.panel_scale).round() as i32;
            ImageList_Draw(self.github_images,0,dc,rc.left+(8.*self.panel_scale) as i32,rc.top+(rc.bottom-rc.top-size)/2,ILD_TRANSPARENT);
            rc.left+=size+(12.*self.panel_scale) as i32;
        }
        let mut text=[0u16;256];let len=GetWindowTextW(hwnd,text.as_mut_ptr(),256);
        let oldfont=SelectObject(dc,self.font);SetBkMode(dc,TRANSPARENT as i32);SetTextColor(dc,fg);
        DrawTextW(dc,text.as_ptr(),len,&mut rc,DT_SINGLELINE|DT_VCENTER|DT_END_ELLIPSIS|DT_NOPREFIX|if push {DT_CENTER}else{DT_LEFT});SelectObject(dc,oldfont);
        if draw.uItemState&CDIS_FOCUS!=0 {let mut focus=draw.rc;InflateRect(&mut focus,-3,-3);DrawFocusRect(dc,&focus);}
        Some(CDRF_SKIPDEFAULT as isize)
    }
}
