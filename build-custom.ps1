param([switch]$Release, [switch]$Preview)
$ErrorActionPreference = 'Stop'
$env:CARGO_HOME='G:\Desktop-Apps\development\toolchain\cargo'
$env:CARGO_TARGET_DIR=Join-Path $PSScriptRoot 'target'
$env:RUSTC='G:\Desktop-Apps\development\toolchain\portable-rust-1.90\bin\rustc.exe'
$env:PATH='G:\Desktop-Apps\development\toolchain\portable-rust-1.90\bin;W:\Dev-Cpp\MinGW64\bin;' + $env:PATH
$env:CXX='W:\Dev-Cpp\MinGW64\bin\g++.exe'
$env:WINDRES='W:\Dev-Cpp\MinGW64\bin\windres.exe'
$env:TEMP='G:\Desktop-Apps\temp'; $env:TMP=$env:TEMP
$env:RUSTFLAGS='-C link-self-contained=yes'
$manifest=Join-Path $PSScriptRoot 'native\Cargo.toml'
if ($Release) { & cargo.exe build --release --locked --manifest-path $manifest }
elseif ($Preview) {
    $env:PET_KEYBOARD_SHOTS='G:\Desktop-Apps\codex-integration\qa-native\quota-keyboard'
    & cargo.exe test --release --locked --manifest-path $manifest keyboard_preview_png -- --ignored --nocapture
}
else { & cargo.exe test --release --locked --manifest-path $manifest }
exit $LASTEXITCODE
