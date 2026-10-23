# TaskbarDock

TaskbarDock da a Windows 11 un aspecto y un comportamiento inspirados en macOS:
un dock flotante con magnificación construido sobre la barra de tareas real y una
barra de menús en la parte superior de la pantalla.

No inyecta código en Explorer, no modifica archivos del sistema y no necesita
privilegios de administrador.

## Dock

- Recorta la barra de tareas nativa en una cápsula ajustada a su contenido, con
  bordes suavizados (antialiasing) y una segunda cápsula opcional para el reloj y
  las notificaciones.
- Magnificación continua estilo macOS: el icono bajo el cursor y sus vecinos
  crecen con una curva suave; el alcance, la intensidad y la velocidad se ajustan.
- Los iconos ampliados ocultan el icono original y el resaltado de Windows,
  conservan el indicador de app abierta y llevan consigo los globos de
  notificación (por ejemplo, el contador de WhatsApp).
- Si el icono del shell no coincide con el de la barra, se amplía el icono real.
- Animación sincronizada con el refresco de pantalla.
- Al maximizar una app o pasar a pantalla completa, la barra vuelve a ocupar
  todo el ancho; en pantalla completa no se dibuja nada encima.
- Los clics, el clic derecho, arrastrar y las vistas previas siguen siendo los
  de Windows.

## Barra superior

- Se reserva su espacio como AppBar; al maximizar una app se oculta y reaparece
  al llevar el cursor al borde superior.
- Logo con menú del sistema (Acerca de, Configuración, Store, Forzar salida,
  Suspender, Reiniciar, Apagar, Bloquear, Cerrar sesión).
- Nombre de la app activa y sus menús reales, leídos con UI Automation.
- Paneles propios estilo macOS que se abren bajo la barra:
  - **Centro de control**: Wi‑Fi y Bluetooth (encender/apagar), modo oscuro,
    captura de pantalla, brillo del monitor, volumen, mezclador por app y
    "Reproduciendo ahora" con controles.
  - **Wi‑Fi**: red actual y redes cercanas.
  - **Sonido**: volumen general y mezclador por aplicación.
  - **Búsqueda**: lanzador de aplicaciones tipo Spotlight.
  - **Calendario**: fecha, mes y acceso a notificaciones.
  - **Límites de uso**: consumo de Claude y Codex (ventana de 5 h y semanal).
- Se oculta en pantalla completa.

## Compilar

En PowerShell:

```powershell
.\build.ps1
```

El ejecutable se genera en `build\Release\TaskbarDock.exe`. Usa el compilador de
.NET Framework 4.x incluido en Windows 11 y los metadatos WinRT del sistema.

## Uso

1. Ejecuta `TaskbarDock.exe`; aparece la ventana de ajustes.
2. Los cambios se aplican al momento.
3. Al cerrar la ventana la app sigue en el área de notificaciones; desde su icono
   se puede abrir los ajustes, activarla o desactivarla y salir.

## Notas técnicas

- Los botones de la barra se leen con UI Automation (`Appid:` en
  `Shell_TrayWnd`) en un hilo aparte y con caché.
- La forma flotante es una región GDI aplicada a `Shell_TrayWnd`; una ventana
  por capas pinta el borde suavizado.
- La magnificación es una ventana por capas con alfa por píxel
  (`UpdateLayeredWindow`).
- El brillo de monitores externos se cambia por DDC/CI (API de configuración de
  monitores); en portátiles, por WMI.
- Los límites de Claude se consultan con la sesión de Claude Code; los de Codex
  se leen de sus registros de sesión locales.

Estas interfaces internas de la barra no están documentadas por Microsoft como
API de personalización; una actualización de Windows puede cambiarlas, por eso
la restauración de la barra original es parte central del diseño.

## Referencias

- [TaskbarXI](https://github.com/ChrisAnd1998/TaskbarXI), consultado solo como
  referencia conceptual. Su código está bajo GPL-3.0 y no se ha copiado.
- [Windhawk Taskbar Configs](https://github.com/SleepyCatHey/Windhawk-Taskbar-Configs),
  referencia visual.
