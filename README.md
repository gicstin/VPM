## Join Our Discord Community
[![Discord](https://img.shields.io/badge/Chat-Discord-blue?logo=discord)](https://discord.gg/TekWBVsa73)

# VPM - Virt-A-Mate Package Manager

<img width="3268" height="1876" alt="image" src="https://github.com/user-attachments/assets/3c700666-794b-48bf-9200-d1859ee61641" />

A fast, modern and open source package manager for Virt-A-Mate. Browse, organize, and optimize your content library without the clutter.

---

## What It Does

VPM helps you wrangle thousands of VAR packages without losing your mind. It scans your library, shows you what you have, and gives you tools to keep things tidy.

<img width="1641" height="1377" alt="image" src="https://github.com/user-attachments/assets/9e1eed01-7ebb-4d49-9187-9ad31dc3af3a" />

### Quick Highlights

- **Fast scanning** - Loads thousands of packages in seconds using parallel processing and smart caching
- **Visual browsing** - See preview images for packages, scenes, and presets at a glance
- **Dependency tracking** - Know exactly what each package needs and what depends on it
- **Texture optimization** - Shrink oversized textures to save disk space and VRAM
- **Hair & light tweaks** - Reduce hair density and shadow resolution for better performance
- **Duplicate cleanup** - Find and remove duplicate package versions
- **Favorites & auto-install** - Mark packages you love, sync with sfishere's var_browser auto-install list

---

## Features

### Optimize Packages

<img width="2378" height="1389" alt="image" src="https://github.com/user-attachments/assets/19b2a98d-cc6a-4b5d-9c63-b489c66d5fe9" />

Repack packages with smaller textures or tweaked settings:

- **Texture resizing** - Downscale 8K textures to 4K, 2K, or 1K
- **Hair density** - Lower hair strand counts for smoother framerates
- **Shadow resolution** - Reduce light shadow maps
- **Mirror disabling** - Turn off mirrors in scenes
- **JSON minification** - Strip whitespace from scene files

All changes create a new optimized package - your originals stay untouched.

---

## Getting Started

1. Download the latest release
2. Run `VPM.exe`
3. Point it at your VAM folder
4. Wait for the initial scan (it's cached after the first run)

That's it. No installer, no registry entries, no admin rights needed.

---

## Requirements

- Windows 10/11 (64-bit)
- .NET 10 Runtime
- A VAM installation with some packages to manage

---

## Built With

- WPF (.NET 10)
- [NetVips](https://github.com/kleisauke/net-vips) for image processing
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) for archive handling
- [ImageListView](https://github.com/oozcitak/imagelistview) for image preview grid

