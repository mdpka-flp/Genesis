English | [Русский](README.ru.md)

# Genesis 🧬

---

### **Genesis** is my school project on particle simulation.  
I was inspired to create this project by cellular automata and YouTube videos about similar simulations.

### Genesis has two modes:
- Standard, where particles are attracted to or repelled from one another depending on the interaction matrix
- Complex, where the interaction matrix contains complex numbers that impart rotation to the particles and allow for even more diverse simulations 

The standard mode can additionally be computed on the GPU (see below). Switching the
computing mode in the settings window keeps the current rules and colours, so the same
rule can be compared in the standard and the complex mode.

### 🎮 GPU acceleration (optional)
Genesis ships with a GPU backend (ILGPU + OpenCL/CUDA). It is a bonus, not a requirement:
if no supported device is found, the app quietly falls back to the CPU backend and shows
`Mode: CPU fallback` in the overlay.

What to install:
- **Windows + NVIDIA** — nothing, CUDA comes with the driver.
- **Windows + AMD** — nothing, OpenCL comes with the Adrenalin driver.
- **Linux + NVIDIA** — only the proprietary driver.
- **Linux + AMD** — the ROCm OpenCL runtime (it is **not** installed by default):
  - Arch / Manjaro: `sudo pacman -S rocm-opencl-runtime`
  - other distributions: install the "ROCm OpenCL" package from your repositories or from AMD.
  Mesa's Rusticl driver is not enough: it reports OpenCL C 1.2 without generic address
  space, which ILGPU requires, so the card is rejected.
- **macOS** is not supported (ILGPU has no Metal backend).

To check which backend is actually used, open the console log at startup or look at the
`Mode: ...` line in the overlay.

### ⬇️ Download
Ready-to-run single-file builds live on the [Releases](https://github.com/mdpka-flp/Genesis/releases) page:
- `Genesis.exe` — Windows x64, just run it;
- `Genesis` — Linux x64, `chmod +x Genesis` and run.

Nothing else has to be installed: the .NET runtime and the native libraries are packed
into the executable (except for the optional OpenCL runtime mentioned above).

### Controls:
Press Tab to open the simulation settings
Q, E — changes the number of particle types
R — randomly distributes particles across the screen
G — generates random simulation rules
F — changes the colors of different particle types to random ones

Colours of individual particle types can be set in the settings window. The "Random ranges"
section at the bottom of that window sets the limits used by random rule generation.

---

### 📡 Contacts
✈️ **Telegram:** [mdpka](https://t.me/mdpka_cs)  
🎥 **YouTube:** [mdpka](https://www.youtube.com/@mdpka_cs)
