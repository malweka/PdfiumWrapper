# PDF Editing Guide

This guide covers creating new PDF documents and adding content using Malweka.PdfiumSdk's page editing API.

## Table of Contents

- [Overview](#overview)
- [Creating Documents](#creating-documents)
- [Adding Pages](#adding-pages)
- [Adding Text](#adding-text)
- [Adding Images](#adding-images)
- [Adding Shapes](#adding-shapes)
- [Page Object Classes](#page-object-classes)
- [Coordinate System](#coordinate-system)
- [Standard Fonts](#standard-fonts)
- [Complete Examples](#complete-examples)
- [Quick Reference](#quick-reference)

---

## Overview

The SDK supports creating and editing PDF documents with the following capabilities:

- Create new PDF documents from scratch
- Add pages with custom dimensions
- Add text objects with custom fonts and styling
- Add image objects (PNG, JPEG, etc.)
- Add path objects (shapes, lines, curves)
- Add rectangles with fill and stroke colors
- Transform and position page objects
- Generate page content and save documents

### Important Workflow

After adding or modifying page objects, you **must** call `GenerateContent()` before saving:

```csharp
page.AddText("Hello", 100, 700);
page.AddRectangle(100, 500, 200, 100, Color.Blue, Color.Black);

page.GenerateContent();  // Required!
document.Save("output.pdf");
```

---

## Creating Documents

### Create an Empty Document

```csharp
using Malweka.PdfiumSdk;

// Create a new empty PDF document
using var document = new PdfDocument();

// Add pages, content, then save
using var page = document.AddPage();
// ... add content ...
page.GenerateContent();
document.Save("new_document.pdf");
```

### Create vs Load

```csharp
// Create NEW document (empty)
using var newDoc = new PdfDocument();

// Load EXISTING document
using var existingDoc = new PdfDocument("existing.pdf");
```

---

## Adding Pages

### Default Page Size (US Letter)

```csharp
// Adds a page at 612 x 792 points (8.5" x 11")
using var page = document.AddPage();
```

### Custom Page Size

```csharp
// A4 Portrait (595 x 842 points)
using var page = document.AddPage(width: 595, height: 842);

// A4 Landscape
using var page = document.AddPage(width: 842, height: 595);

// Custom size
using var page = document.AddPage(width: 400, height: 600);
```

### Insert Page at Specific Position

```csharp
// Insert at the beginning (index 0)
using var page = document.AddPage(width: 612, height: 792, index: 0);

// Insert at position 2 (third page)
using var page = document.AddPage(width: 612, height: 792, index: 2);
```

### Common Page Sizes

| Size | Width (points) | Height (points) | Inches |
|------|----------------|-----------------|--------|
| US Letter | 612 | 792 | 8.5" × 11" |
| US Legal | 612 | 1008 | 8.5" × 14" |
| A4 | 595 | 842 | 8.27" × 11.69" |
| A3 | 842 | 1191 | 11.69" × 16.54" |
| A5 | 420 | 595 | 5.83" × 8.27" |

---

## Adding Text

### Basic Text

```csharp
var text = page.AddText("Hello World", x: 100, y: 700);
```

### Styled Text

```csharp
var text = page.AddText("Hello World", x: 100, y: 700);
text.Font = "Helvetica";
text.FontSize = 24;
text.Color = Color.Black;
```

### Text with Font Specified Inline

```csharp
var text = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica", fontSize: 12);
text.Color = Color.DarkBlue;
```

### Multiple Text Objects

```csharp
// Title
var title = page.AddText("Document Title", x: 100, y: 750);
title.Font = "Helvetica-Bold";
title.FontSize = 24;
title.Color = Color.Black;

// Subtitle
var subtitle = page.AddText("A sample document", x: 100, y: 720);
subtitle.Font = "Helvetica";
subtitle.FontSize = 14;
subtitle.Color = Color.Gray;

// Body text
var body = page.AddText("This is the main content of the document.", x: 100, y: 680);
body.Font = "Times-Roman";
body.FontSize = 12;
body.Color = Color.Black;
```

---

## Adding Images

### From File

```csharp
var imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

### From Byte Array

```csharp
// Image bytes from any source (database, HTTP response, etc.)
byte[] imageBytes = GetImageFromDatabase();
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

### Supported Formats

Images are decoded using SkiaSharp, so the following formats are supported:

- PNG
- JPEG
- WebP
- GIF
- BMP

### Image Positioning

The `x` and `y` parameters specify the **bottom-left corner** of the image:

```csharp
// Image positioned at bottom-left corner (100, 500)
// Extends 200 points right and 100 points up
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

---

## Adding Shapes

### Rectangle

```csharp
// Filled rectangle with stroke
var rect = page.AddRectangle(
    x: 100, 
    y: 400, 
    width: 200, 
    height: 100,
    fillColor: Color.LightBlue,
    strokeColor: Color.Black
);
rect.StrokeWidth = 2;
```

### Rectangle (Stroke Only)

```csharp
// Border only, no fill
var border = page.AddRectangle(
    x: 50, 
    y: 50, 
    width: 512, 
    height: 692,
    fillColor: null,  // No fill
    strokeColor: Color.Gray
);
border.StrokeWidth = 1;
```

### Custom Path (Triangle)

```csharp
var triangle = page.AddPath();
triangle.MoveTo(150, 300);   // Start at top
triangle.LineTo(100, 200);   // Down to bottom-left
triangle.LineTo(200, 200);   // Across to bottom-right
triangle.Close();            // Back to start

triangle.FillColor = Color.Red;
triangle.StrokeColor = Color.Black;
triangle.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

### Custom Path (Complex Shape)

```csharp
var path = page.AddPath();

// Draw a house shape
path.MoveTo(100, 200);   // Bottom-left
path.LineTo(100, 300);   // Up left wall
path.LineTo(150, 350);   // Up to roof peak
path.LineTo(200, 300);   // Down right roof
path.LineTo(200, 200);   // Down right wall
path.Close();            // Bottom

path.FillColor = Color.LightYellow;
path.StrokeColor = Color.Brown;
path.StrokeWidth = 2;
path.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

### Path with Curves (Bézier)

```csharp
var curve = page.AddPath();
curve.MoveTo(100, 300);
curve.BezierTo(
    150, 400,   // Control point 1
    200, 400,   // Control point 2
    250, 300    // End point
);
curve.StrokeColor = Color.Blue;
curve.StrokeWidth = 2;
curve.SetDrawMode(PdfPathFillMode.None, stroke: true);
```

### Fluent Path API

```csharp
var path = page.AddPath();
path.MoveTo(100, 300)
    .LineTo(200, 300)
    .LineTo(150, 200)
    .Close();

path.FillColor = Color.Green;
path.StrokeColor = Color.DarkGreen;
path.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

---

## Page Object Classes

### PdfPageObject (Base Class)

Base class for all page objects with common functionality:

| Property/Method | Description |
|----------------|-------------|
| `GetBounds()` | Get the bounding rectangle |
| `GetMatrix()` | Get the transformation matrix |
| `SetMatrix()` | Set the transformation matrix |
| `HasTransparency` | Check if object has transparency |
| `Transform()` | Apply transformation |

### PdfTextObject

Represents text on a PDF page.

| Property | Type | Description |
|----------|------|-------------|
| `Font` | `string` | Font name (e.g., "Helvetica") |
| `FontSize` | `float` | Font size in points |
| `Color` | `Color` | Text color |

### PdfImageObject

Represents images on a PDF page.

| Property/Method | Description |
|----------------|-------------|
| Position set via constructor | `x`, `y`, `width`, `height` |

### PdfPathObject

Represents vector shapes and paths.

| Property/Method | Description |
|----------------|-------------|
| `MoveTo(x, y)` | Move to point (start new subpath) |
| `LineTo(x, y)` | Draw line to point |
| `BezierTo(...)` | Draw cubic Bézier curve |
| `Close()` | Close the current subpath |
| `FillColor` | Fill color |
| `StrokeColor` | Stroke (outline) color |
| `StrokeWidth` | Stroke width in points |
| `SetDrawMode(fillMode, stroke)` | Set fill and stroke behavior |

### PdfPathFillMode

| Value | Description |
|-------|-------------|
| `None` | No fill |
| `Alternate` | Alternate fill rule (even-odd) |
| `Winding` | Winding fill rule (non-zero) |

---

## Coordinate System

PDF uses a coordinate system where:

- **Origin (0, 0)** is at the **bottom-left corner** of the page
- **X axis** increases to the **right**
- **Y axis** increases **upward**
- **Units** are in **points** (1 point = 1/72 inch)

```
        Y
        ^
        |
        |  (100, 700) "Hello"
        |
        |  (100, 500) [Image]
        |
        |  (100, 300) [Triangle]
        |
(0,0)   +-------------------------> X
```

### Converting Between Units

```csharp
// Inches to points
float points = inches * 72;

// Points to inches
float inches = points / 72;

// Millimeters to points
float points = mm * 72 / 25.4f;

// Points to millimeters
float mm = points * 25.4f / 72;
```

---

## Standard Fonts

The following fonts are built into PDF and don't require embedding:

| Font Family | Variants |
|-------------|----------|
| Helvetica | Helvetica, Helvetica-Bold, Helvetica-Oblique, Helvetica-BoldOblique |
| Times | Times-Roman, Times-Bold, Times-Italic, Times-BoldItalic |
| Courier | Courier, Courier-Bold, Courier-Oblique, Courier-BoldOblique |
| Symbol | Symbol |
| ZapfDingbats | ZapfDingbats |

```csharp
// Using standard fonts
text.Font = "Helvetica";
text.Font = "Helvetica-Bold";
text.Font = "Times-Roman";
text.Font = "Courier";
```

---

## Complete Examples

### Example 1: Simple Document

```csharp
using Malweka.PdfiumSdk;
using System.Drawing;

using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792);

// Add title
var title = page.AddText("Hello World", x: 100, y: 700);
title.Font = "Helvetica";
title.FontSize = 24;
title.Color = Color.Black;

// Add body text
var body = page.AddText("This is a sample PDF created with Malweka.PdfiumSdk", x: 100, y: 650);
body.Font = "Helvetica";
body.FontSize = 12;
body.Color = Color.Gray;

page.GenerateContent();
document.Save("hello_world.pdf");
```

### Example 2: Document with Image

```csharp
using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792);

// Add title
var title = page.AddText("Document with Image", x: 100, y: 700);
title.Font = "Helvetica-Bold";
title.FontSize = 18;
title.Color = Color.Black;

// Add image
var imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

// Add caption
var caption = page.AddText("Figure 1: Company Logo", x: 100, y: 480);
caption.Font = "Helvetica-Oblique";
caption.FontSize = 10;
caption.Color = Color.Gray;

page.GenerateContent();
document.Save("with_image.pdf");
```

### Example 3: Multi-Page Document

```csharp
using var document = new PdfDocument();

for (int i = 0; i < 3; i++)
{
    using var page = document.AddPage();
    
    // Page title
    var title = page.AddText($"Page {i + 1}", x: 250, y: 750);
    title.Font = "Helvetica-Bold";
    title.FontSize = 24;
    title.Color = Color.DarkBlue;
    
    // Page border
    var border = page.AddRectangle(
        x: 50, y: 50, 
        width: 512, height: 692,
        fillColor: null, 
        strokeColor: Color.Gray
    );
    border.StrokeWidth = 1;
    
    // Content
    var content = page.AddText($"This is the content of page {i + 1}.", x: 100, y: 600);
    content.Font = "Times-Roman";
    content.FontSize = 12;
    content.Color = Color.Black;
    
    page.GenerateContent();
}

document.Save("multipage.pdf");
```

### Example 4: Shapes and Graphics

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

// Title
var title = page.AddText("Shapes Demo", x: 250, y: 750);
title.Font = "Helvetica-Bold";
title.FontSize = 20;
title.Color = Color.Black;

// Rectangle
var rect = page.AddRectangle(x: 100, y: 600, width: 150, height: 80,
    fillColor: Color.LightBlue, strokeColor: Color.Blue);
rect.StrokeWidth = 2;

// Triangle
var triangle = page.AddPath();
triangle.MoveTo(350, 680);
triangle.LineTo(300, 600);
triangle.LineTo(400, 600);
triangle.Close();
triangle.FillColor = Color.LightGreen;
triangle.StrokeColor = Color.Green;
triangle.SetDrawMode(PdfPathFillMode.Winding, stroke: true);

// Circle approximation (using Bézier curves)
var circle = page.AddPath();
float cx = 200, cy = 450, r = 50;
float k = 0.552284749831f; // Magic number for circle approximation
circle.MoveTo(cx + r, cy);
circle.BezierTo(cx + r, cy + r * k, cx + r * k, cy + r, cx, cy + r);
circle.BezierTo(cx - r * k, cy + r, cx - r, cy + r * k, cx - r, cy);
circle.BezierTo(cx - r, cy - r * k, cx - r * k, cy - r, cx, cy - r);
circle.BezierTo(cx + r * k, cy - r, cx + r, cy - r * k, cx + r, cy);
circle.FillColor = Color.LightCoral;
circle.StrokeColor = Color.DarkRed;
circle.SetDrawMode(PdfPathFillMode.Winding, stroke: true);

page.GenerateContent();
document.Save("shapes.pdf");
```

---

## Quick Reference

```csharp
// Create document
using var document = new PdfDocument();

// Add page
using var page = document.AddPage(width: 612, height: 792);

// Add text
var text = page.AddText("Hello", x: 100, y: 700, font: "Helvetica", fontSize: 12);
text.Color = Color.Black;

// Add image
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

// Add rectangle
var rect = page.AddRectangle(x: 100, y: 400, width: 200, height: 100,
    fillColor: Color.LightBlue, strokeColor: Color.Black);

// Add path
var path = page.AddPath();
path.MoveTo(100, 300).LineTo(200, 300).LineTo(150, 200).Close();
path.FillColor = Color.Red;
path.StrokeColor = Color.Black;
path.SetDrawMode(PdfPathFillMode.Winding, stroke: true);

// Save
page.GenerateContent();  // Required!
document.Save("output.pdf");
```

---

## Notes

1. **Always call `GenerateContent()`** after adding or modifying page objects, before saving.

2. **Page objects are disposable** — they are automatically disposed when removed from a page or when the page is disposed.

3. **Images are decoded using SkiaSharp** and converted to PDFium's internal format.

4. **Custom fonts** can be loaded using `FPDFText_LoadFont` for embedding non-standard fonts.

5. **Coordinate system** — remember that Y increases upward, and (0,0) is at the bottom-left.
