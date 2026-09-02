# 🎧 VolumeMixer Tray App

A lightweight Windows tray utility that provides quick access to either the **Legacy Volume Mixer (SndVol.exe)** or the Windows **Sound Output** flyout, with modern enhancements.

Opens instantly from the tray and includes **theme-aware tray icons**, **auto monitor detection** for the Legacy mixer, **automatic mouse-distance closing** for both tray-opening modes, and **optional auto-start setup**.

---

## ⭐ Key Features

- Runs quietly in the **system tray**
- **Left-click** → Opens the selected tray target
  - **Legacy Volume Mixer** — the traditional `SndVol.exe` mixer
  - **Sound Output** — opens the Windows Sound Output flyout by simulating **Win+Ctrl+V**
- **Right-click** → Context menu with settings
- **Open from tray** submenu lets you choose which target left-click opens
- Your tray-opening choice is saved and restored automatically
- Auto-detects the **current monitor** for the Legacy Volume Mixer
- Positions the Legacy mixer at **bottom-right** of the active screen
- **Auto-closes after the mouse moves farther than the configured distance** in both Legacy and Sound Output modes
- **Theme-aware icons** (auto, light, dark)
- Portable — all files in one folder, no installer
- Compiles easily using the included **Build.bat**

---

## 🖱 Tray Opening Mode

Right-click the tray icon and open:

**Open from tray**

You can choose:

- **Legacy Volume Mixer** — opens the traditional Windows `SndVol.exe` mixer. The existing monitor-aware positioning and automatic closing behavior are preserved.
- **Sound Output** — simulates **Win+Ctrl+V**, opening the Windows Sound Output device flyout. The same mouse-distance watcher is also active in this mode.

The selected option is marked with a checkmark and is stored in the current user's registry, so it remains selected after restarting the app or Windows.

---

## ✨ Auto-Close Behavior

The same mouse-distance auto-close system is used for both tray-opening modes.

When either mode is opened:

- The current mouse position is recorded as the starting point.
- A short grace period prevents immediate closure while the interface is opening.
- The existing configurable **distance** (`distancePx`) determines how far the mouse can move before the interface is dismissed.
- The existing bottom-right safe zone remains available to make movement from the tray toward the interface comfortable.

For the **Legacy Volume Mixer**, the existing `SndVol.exe` window is closed gracefully.

For **Sound Output**, the Windows shell flyout is dismissed using **Escape**, without changing the selected audio device or volume.

---

## 🎨 Theme & Icon Handling

The app includes **light and dark tray icons**:

- Windows in **dark mode** → App shows **light icon**
- Windows in **light mode** → App shows **dark icon**

You may manually override via tray menu:

- Auto (follow Windows)
- Light icon
- Dark icon

---

## 🖥 Monitor-Aware Legacy Mixer Positioning

The monitor-aware positioning behavior applies to the **Legacy Volume Mixer** mode:

- Detects which screen the mouse is on
- Opens the Legacy mixer on that same screen
- Anchors to **bottom-right corner** (adjusted for taskbar position)
- If the `-t` parameter is ignored by Windows, the app force-moves the window

Sound Output uses the Windows flyout directly and therefore does not need Legacy window positioning.

---

## 🛠 How to Build (No Visual Studio Required)

Place these files together in the same folder:

```
Vol-Mixer-Tray.cs
Build.bat
volmixer.ico
volmixer_black.ico
batch_g2_VolMixer.ico
```

Then run:

```
Build.bat
```

This generates:

```
VolMixerTray.exe
```

---

## 📂 Project Structure

```
VolumeMixer_TrayApp/
│
├── Vol-Mixer-Tray.cs          # Main source code
├── Build.bat                  # Build script (csc.exe)
├── volmixer.ico               # Dark theme tray icon
├── volmixer_black.ico         # Light theme tray icon
├── batch_g2_VolMixer.ico      # Executable icon
└── README.md
```

---

# ▶ Auto-Start the Application (Optional)

VolumeMixer Tray App includes a built-in **Run at Startup** option in the tray menu.

This setting applies to the **current Windows user only** and uses the user's `Run` registry key, so **administrator permissions are not required**.

---

## 🟩 Auto-Start for Current User Only

Right-click the VolumeMixer Tray App icon and enable:

**Run at Startup**

The app stores its startup entry in:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
```

The startup value is named:

```text
VolumeMixerTray
```

✔ Starts automatically when **your Windows user account logs in**  
✔ No administrator permissions required  
✔ Can be enabled or disabled directly from the tray menu

---

### Notes

- The startup option does **not** install the application or copy files to a Startup folder.
- The executable remains portable; Windows simply launches the configured executable path at user logon.
- The setting is stored for the **current Windows user** and does not configure startup for other users.

---

## 📦 Included Build Script (`Build.bat`)

```bat
@echo off
setlocal

REM Build script for VolumeMixer Tray App (portable compiler)

set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

%CSC% ^
 /nologo /target:winexe /optimize+ ^
 /win32icon:"batch_g2_VolMixer.ico" ^
 /resource:"volmixer.ico",VolMixerTray.Icons.Dark.ico ^
 /resource:"volmixer_black.ico",VolMixerTray.Icons.Light.ico ^
 /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
 Vol-Mixer-Tray.cs

echo.
echo Build complete! If no errors were shown, VolMixerTray.exe is ready.
echo.
pause
```

---

## 📄 License

Licensed under the **MIT License**.
You may modify, distribute, and use the software freely.

---

## 🙌 Contributions & Feedback

Issues and pull requests are welcome!
Feel free to suggest features, improve code, or submit translations.
