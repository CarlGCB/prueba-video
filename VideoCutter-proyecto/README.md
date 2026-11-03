# VideoCutter

Programa para **Windows 11** que corta y une vídeos en tu ordenador, sin límites de tamaño ni de memoria del navegador.

## Qué hace
1. **Añadir vídeos:** sueltas muchos a la vez. Los MP4 pasan directos a la lista; los demás (AVI, MKV…) se convierten a MP4 de uno en uno.
   Si ya llevan H.264 con audio AAC/MP3 solo se cambia el contenedor (segundos); si no, se recodifican.
2. **Editor con diapositivas:** arrastra para ordenarlas (ese es el orden final), previsualiza cada vídeo, ajusta inicio y fin,
   y quita el principio a unos vídeos (p. ej. 35 s) y a otros (p. ej. 31 s) usando marcas y «En los marcados / NO marcados / todos».
3. **Exportar** a **un solo MP4** con índice (se puede saltar sin tirones):
   - *Recodificar:* fotogramas clave cada 2 s, audio limpio, mismos parámetros para todos (une vídeos distintos). Admite NVIDIA, Intel y AMD.
   - *Copiar:* sin recodificar, tarda lo que tarde el disco. Requiere vídeos con el mismo códec, resolución y fps.
   - Cuenta atrás detallada, botón Cancelar, y el equipo no se suspende mientras trabaja.

## Obtener el programa (.exe)
**Opción A, sin instalar nada (recomendada):**
1. Sube esta carpeta a un repositorio de GitHub.
2. Pestaña **Actions** → «Compilar VideoCutter» → espera ~5 min.
3. Entra en la ejecución y descarga el archivo **VideoCutter-Windows** (contiene `VideoCutter.exe`).

**Opción B, en tu PC:**
```
winget install Microsoft.DotNet.SDK.8
dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
El programa queda en `publish\VideoCutter.exe`.

## Primer uso
- Al abrirlo por primera vez pedirá descargar **FFmpeg** (unos 200 MB, una sola vez; se guarda en `%LOCALAPPDATA%\VideoCutter\ffmpeg`).
  Si prefieres instalarlo tú: `winget install Gyan.FFmpeg`.
- Windows puede avisar con «SmartScreen» porque el programa no está firmado: *Más información → Ejecutar de todas formas*.
- Los vídeos convertidos se guardan en `Vídeos\VideoCutter\Convertidos`; el resultado, donde elijas (o en `Vídeos\VideoCutter`).

## Para desarrolladores
- `src/Core`: motor (ffprobe, ffmpeg, exportación, conversión). Sin dependencias de Windows.
- `src/App`: ventana WPF.
- `tests/CoreTests`: pruebas del motor con FFmpeg real (`dotnet run --project tests/CoreTests`).
