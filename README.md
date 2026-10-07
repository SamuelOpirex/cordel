# Cordel

**[Descargar Cordel para Windows 10/11 ›](https://github.com/SamuelOpirex/cordel/releases/latest)** · portable, sin instalar nada.

Port para Windows de [Tendedero](https://github.com/alejandrobujan/tendedero) (macOS, de Alejandro Buján, MIT).
Por su licencia, las versiones modificadas no pueden usar el nombre ni el icono de Tendedero, así que esta se llama Cordel.

Tus capturas vuelan desde donde las hiciste y se cuelgan de un cordel en lo alto de la **pantalla principal** (aunque la captura se haga en la secundaria). Como Windows no guarda la zona capturada, se deduce del ratón: el arrastre del recorte, la ventana pulsada o la pantalla completa.

| Gesto | Acción |
|:--|:--|
| Apoyar el puntero en el borde superior | Baja el cordel |
| Ctrl+Alt+T o clic en el icono de la bandeja | Mostrar / ocultar |
| Clic | Copiar la imagen |
| Doble clic | Abrir |
| Mantener pulsado | Editar en Paint |
| Arrastrar a una app | Envía una copia; se queda colgada |
| Arrastrar a una carpeta o a la Papelera | Se va allí y sale del cordel |
| Clic en la pinza | Descolgarla: cae por la pantalla (el archivo se queda; las del portapapeles van a la Papelera) |
| Rueda del ratón sobre el cordel | Recorrer las fotos antiguas, apiladas a la izquierda (hasta 50) |

Vigila la carpeta *Imágenes\Capturas de pantalla* y lo que copia la Herramienta Recortes.
Opcional en la bandeja: colgar también imágenes copiadas desde otras aplicaciones.

## Compilar

Requisitos: Windows 10/11 (trae .NET Framework 4.8) y Visual Studio 2022 en cualquier edición,
incluida la gratuita Community, o las Build Tools de Visual Studio 2022, con la carga de trabajo
«Desarrollo de escritorio de .NET».

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

El resultado queda en `bin\Cordel.exe`. No hay XAML ni proyecto: el script llama directamente al
compilador de C#, dibuja el icono con el propio programa y lo incrusta en una segunda pasada.

## Dónde está cada cosa

| Archivo | Qué hace |
|:--|:--|
| `Controller.cs` | Arranque, bandeja, atajo Ctrl+Alt+T, cuándo baja y se recoge el cordel, acciones sobre cada foto, portapapeles |
| `LineView.cs` | La ventana transparente, el cordel, las fotos y su física (balanceo, brisa, montón, desplazamiento), gestos |
| `Flight.cs` | De dónde salió la captura (ratón) y la animación de vuelo y caída sobre la pantalla |
| `Support.cs` | Ajustes, carpetas, carga de imágenes, sonidos, vigilancia de la carpeta de capturas, icono |
| `Native.cs` | Llamadas a Win32: cursor, pantalla completa, arrastre con el objeto de datos del Explorador |
| `pinza.png` | La pinza de madera, incrustada como recurso |
| `app.manifest` | DPI por monitor |

Ajustes útiles: medidas del cordel y de la pinza en la clase `Layout` (`LineView.cs`), límite de fotos
`maxItems` (`Controller.cs`), duraciones del vuelo en `Flight` (`Flight.cs`).

## Licencia

MIT, como el original. Ver `LICENSE`. El nombre y el icono de Tendedero no se incluyen.
