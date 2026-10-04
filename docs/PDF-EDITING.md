# PDF Editing Guide

This guide covers creating PDF documents, adding content to new or existing pages, and reading the objects already on a page with PdfiumWrapper's page editing API.

## Table of Contents

- [Overview](#overview)
- [Creating Documents](#creating-documents)
- [Adding Pages](#adding-pages)
- [Adding Text](#adding-text)
- [Adding Images](#adding-images)
- [Adding Shapes](#adding-shapes)
- [Editing Existing Documents](#editing-existing-documents)
- [Reading Page Objects](#reading-page-objects)
- [Removing Page Objects](#removing-page-objects)
- [Page Object Classes](#page-object-classes)
- [Coordinate System](#coordinate-system)
- [Standard Fonts](#standard-fonts)
- [Complete Examples](#complete-examples)
- [Quick Reference](#quick-reference)
- [Notes](#notes)

---

## Overview

The library supports:

- Creating new PDF documents from scratch
- Adding pages with custom dimensions
- Adding text in one of the 14 standard PDF fonts, with size and color
- Adding PNG and JPEG images
- Adding paths (lines, curves, shapes) and rectangles with fill and stroke colors
- Transforming and positioning page objects
- Adding content to pages of an existing PDF
- Reading the objects on a page (text, images, paths, shadings, form XObjects) and removing them
- Generating page content and saving documents

### Important Workflow

After adding, changing or removing page objects, you **must** call `GenerateContent()` on the page before saving:

```csharp
page.AddText("Hello", 100, 700);
page.AddRectangle(100, 500, 200, 100, Color.Blue, Color.Black);

page.GenerateContent();  // Required!
document.Save("output.pdf");
```

The samples use `System.Drawing.Color`. Add `using System.Drawing;` to your file.

---

## Creating Documents

### Create an Empty Document

```csharp
using PdfiumWrapper;
using System.Drawing;

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
using var a4 = document.AddPage(width: 595, height: 842);

// A4 Landscape
using var a4Landscape = document.AddPage(width: 842, height: 595);

// Custom size
using var custom = document.AddPage(width: 400, height: 600);
```

### Insert Page at Specific Position

```csharp
// Insert at the beginning (index 0)
using var cover = document.AddPage(width: 612, height: 792, index: 0);

// Insert at position 2 (third page)
using var third = document.AddPage(width: 612, height: 792, index: 2);
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
// Helvetica, 12 pt by default
var text = page.AddText("Hello World", x: 100, y: 700);
```

`x` and `y` are the start of the text's baseline.

### Font, Size and Color

```csharp
var title = page.AddText("Document Title", x: 100, y: 750, font: "Helvetica-Bold", fontSize: 24);
title.Color = Color.Black;

var subtitle = page.AddText("A sample document", x: 100, y: 720, font: "Helvetica", fontSize: 14);
subtitle.Color = Color.Gray;

var body = page.AddText("This is the main content of the document.", x: 100, y: 680, font: "Times-Roman", fontSize: 12);
body.Color = Color.Black;
```

The font is chosen when the text is added and cannot be changed afterwards; the size can (`FontSize` has a getter and a setter). Only the [standard fonts](#standard-fonts) are available: `AddText` throws `InvalidOperationException` for any other font name.

---

## Adding Images

```csharp
// From a file, or bytes from any other source (database, HTTP response, ...)
byte[] imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

// Move or resize it later
image.SetPositionAndSize(x: 120, y: 480, width: 240, height: 120);
```

### Supported Formats

PNG and JPEG only. They are decoded with libpng and libjpeg-turbo. Any other format throws `NotSupportedException`. An image larger than the render pixel limit (268,435,456 pixels by default, see [Troubleshooting](TROUBLESHOOTING.md)) throws `InvalidOperationException`.

### Image Positioning

The `x` and `y` parameters specify the **bottom-left corner** of the image:

```csharp
// Bottom-left corner at (100, 500); extends 200 points right and 100 points up
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);
```

---

## Adding Shapes

A path or rectangle is only drawn if it has a fill mode or a stroke. `AddRectangle` sets the draw mode from the colors you pass; with both colors `null` the rectangle is invisible. A path from `AddPath()` draws nothing until you call `SetDrawMode`.

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
curve.LineCap = PdfLineCapStyle.Round;
curve.SetDrawMode(PdfPathFillMode.None, stroke: true);
```

### Fluent Path API

`MoveTo`, `LineTo`, `BezierTo` and `Close` return the path:

```csharp
var house = page.AddPath();
house.MoveTo(100, 200)   // Bottom-left
    .LineTo(100, 300)    // Left wall
    .LineTo(150, 350)    // Roof peak
    .LineTo(200, 300)    // Right roof
    .LineTo(200, 200)    // Right wall
    .Close();            // Floor

house.FillColor = Color.LightYellow;
house.StrokeColor = Color.Brown;
house.StrokeWidth = 2;
house.LineJoin = PdfLineJoinStyle.Round;
house.SetDrawMode(PdfPathFillMode.Winding, stroke: true);
```

---

## Editing Existing Documents

The same methods add content to pages of a loaded PDF. Load the document, get the page, add objects, call `GenerateContent()` and save to a new file:

```csharp
using var document = new PdfDocument("contract.pdf");

using (var page = document.GetPage(0))
{
    var stamp = page.AddText("APPROVED", x: 400, y: 750, font: "Helvetica-Bold", fontSize: 24);
    stamp.Color = Color.Red;

    page.AddRectangle(390, 740, 160, 36, fillColor: null, strokeColor: Color.Red);

    page.GenerateContent();
}

document.Save("contract_approved.pdf");
```

`PdfDocument.DeletePage(index)` removes a page. To combine, reorder or extract pages across documents, use `PdfMerger` (see [Examples](EXAMPLES.md#pdf-merging)).

---

## Reading Page Objects

`PdfPage.ObjectCount` and `PdfPage.GetObject(index)` list the objects on a page. `GetObject` returns the wrapper type that matches the object's kind, so use pattern matching:

```csharp
using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0);

for (int i = 0; i < page.ObjectCount; i++)
{
    PdfPageObject obj = page.GetObject(i);
    var (left, bottom, right, top) = obj.GetBounds();

    switch (obj)
    {
        case PdfTextObject text:
            Console.WriteLine($"Text, {text.FontSize} pt, at ({left:0}, {bottom:0})");
            break;

        case PdfImageObject image:
            // The image's own pixels as tightly packed BGRA (stride = width * 4), or null
            RawBitmap? pixels = image.GetBitmap();
            Console.WriteLine($"Image {pixels?.Width} x {pixels?.Height} px, drawn at ({left:0}, {bottom:0})-({right:0}, {top:0})");
            break;

        case PdfPathObject path:
            Console.WriteLine($"Path with {path.SegmentCount} segments");
            break;

        case PdfFormObject form:
            Console.WriteLine($"Form XObject with {form.ObjectCount} sub-objects");
            break;

        case PdfShadingObject:
            Console.WriteLine("Shading");
            break;
    }
}
```

A form XObject contains its own objects. `PdfFormObject.GetObject(index)` returns them the same way, so walk them recursively:

```csharp
static void Walk(PdfFormObject form, int depth)
{
    for (int i = 0; i < form.ObjectCount; i++)
    {
        PdfPageObject child = form.GetObject(i);
        Console.WriteLine($"{new string(' ', depth * 2)}{child.GetType().Name}");

        if (child is PdfFormObject nested)
            Walk(nested, depth + 1);
    }
}
```

`PdfImageObject.GetRenderedBitmap(page)` returns the image as it appears on the page, with its mask and transformation applied. Both bitmap methods return `RawBitmap?` and copy the pixels into managed memory, so there is nothing to release.

### Ownership

- The page owns the objects `GetObject` returns. Disposing a wrapper does not delete the object, and the wrapper becomes unusable when the page (or its document) is disposed.
- While a wrapper is not disposed, `GetObject` returns the same wrapper for the same object.
- Sub-objects returned by `PdfFormObject.GetObject` belong to the form object and are unusable once it is disposed.

---

## Removing Page Objects

`PdfPage.RemoveObject(obj)` takes an object off the page. After a successful removal **you own the object**: dispose it, or the document destroys it when the document is disposed.

```csharp
using var document = new PdfDocument("document.pdf");
using var page = document.GetPage(0);

// Remove every image. Go backwards: removing an object shifts the indices after it.
for (int i = page.ObjectCount - 1; i >= 0; i--)
{
    if (page.GetObject(i) is PdfImageObject image && page.RemoveObject(image))
    {
        image.Dispose();
    }
}

page.GenerateContent();
document.Save("without_images.pdf");
```

`RemoveObject`:

- returns `false` when the object is not on this page: an object of another page, a sub-object of a form object, or one already removed;
- throws `ObjectDisposedException` when the wrapper you pass is disposed;
- throws `ArgumentNullException` for `null`.

---

## Page Object Classes

You never construct page objects yourself: `AddText`, `AddImage`, `AddPath` and `AddRectangle` create them, and `GetObject` wraps existing ones.

### PdfPageObject (Base Class)

| Member | Description |
|--------|-------------|
| `ObjectType` | Raw object kind (`int`); prefer pattern matching on the wrapper type |
| `GetBounds()` | Bounding box as `(left, bottom, right, top)` |
| `GetMatrix()` / `SetMatrix(a, b, c, d, e, f)` | Get or replace the transformation matrix |
| `Transform(a, b, c, d, e, f)` | Multiply the current matrix by another |
| `HasTransparency` | Whether the object uses transparency |
| `Dispose()` | Destroys an object you own (removed from its page); does nothing to an object still on a page |

### PdfTextObject

| Member | Type | Description |
|--------|------|-------------|
| `Text` | `string` (set only) | Replace the text |
| `FontSize` | `float` (get/set) | Font size in points |
| `Color` | `Color` (set only) | Fill color |
| `StrokeColor` | `Color` (set only) | Outline color |

The font is fixed when the text is added (`AddText(text, x, y, font, fontSize)`).

### PdfImageObject

| Member | Description |
|--------|-------------|
| `SetPositionAndSize(x, y, width, height)` | Place the image; `x`, `y` is the bottom-left corner |
| `GetBitmap()` | The image's own pixels as BGRA, `RawBitmap?` |
| `GetRenderedBitmap(page)` | The image as drawn on the page, `RawBitmap?` |

### PdfPathObject

| Member | Description |
|--------|-------------|
| `MoveTo(x, y)` | Start a new subpath |
| `LineTo(x, y)` | Line to a point |
| `BezierTo(x1, y1, x2, y2, x3, y3)` | Cubic Bézier curve |
| `Close()` | Close the current subpath |
| `FillColor` | Fill color (set only) |
| `StrokeColor` | Stroke color (set only) |
| `StrokeWidth` | Stroke width in points (get/set) |
| `LineJoin` | `PdfLineJoinStyle`: `Miter`, `Round`, `Bevel` (set only) |
| `LineCap` | `PdfLineCapStyle`: `Butt`, `Round`, `Square` (set only) |
| `SegmentCount` | Number of path segments |
| `SetDrawMode(fillMode, stroke)` | Fill rule and whether to stroke; required for the path to be drawn |

### PdfPathFillMode

| Value | Description |
|-------|-------------|
| `None` | No fill |
| `Alternate` | Alternate fill rule (even-odd) |
| `Winding` | Winding fill rule (non-zero) |

### PdfFormObject and PdfShadingObject

`PdfFormObject` (a form XObject) has `ObjectCount` and `GetObject(index)` for its sub-objects. `PdfShadingObject` (a smooth gradient) has only the base class members. Both are read from existing PDFs; they cannot be created.

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
static float InchesToPoints(float inches) => inches * 72;
static float PointsToInches(float points) => points / 72;
static float MillimetersToPoints(float mm) => mm * 72 / 25.4f;
static float PointsToMillimeters(float points) => points * 25.4f / 72;
```

---

## Standard Fonts

These fonts are built into PDF readers and need no embedding. They are the only fonts `AddText` accepts:

| Font Family | Variants |
|-------------|----------|
| Helvetica | Helvetica, Helvetica-Bold, Helvetica-Oblique, Helvetica-BoldOblique |
| Times | Times-Roman, Times-Bold, Times-Italic, Times-BoldItalic |
| Courier | Courier, Courier-Bold, Courier-Oblique, Courier-BoldOblique |
| Symbol | Symbol |
| ZapfDingbats | ZapfDingbats |

```csharp
// The font is chosen when the text is added; PDFium cannot change it afterwards
var heading = page.AddText("Heading", x: 100, y: 700, font: "Helvetica-Bold", fontSize: 18);
var code = page.AddText("var x = 1;", x: 100, y: 670, font: "Courier");
code.FontSize = 10; // the size can be changed later
```

---

## Complete Examples

### Example 1: Simple Document

```csharp
using PdfiumWrapper;
using System.Drawing;

using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792);

var title = page.AddText("Hello World", x: 100, y: 700, font: "Helvetica", fontSize: 24);
title.Color = Color.Black;

var body = page.AddText("This is a sample PDF created with PdfiumWrapper", x: 100, y: 650, font: "Helvetica", fontSize: 12);
body.Color = Color.Gray;

page.GenerateContent();
document.Save("hello_world.pdf");
```

### Example 2: Document with Image

```csharp
using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792);

var title = page.AddText("Document with Image", x: 100, y: 700, font: "Helvetica-Bold", fontSize: 18);
title.Color = Color.Black;

var imageBytes = File.ReadAllBytes("logo.png");
page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

var caption = page.AddText("Figure 1: Company Logo", x: 100, y: 480, font: "Helvetica-Oblique", fontSize: 10);
caption.Color = Color.Gray;

page.GenerateContent();
document.Save("with_image.pdf");
```

### Example 3: Multi-Page Document

```csharp
using var document = new PdfDocument();
const int pageCount = 3;

for (int i = 0; i < pageCount; i++)
{
    using var page = document.AddPage();

    var header = page.AddText($"Page {i + 1} of {pageCount}", x: 250, y: 750, font: "Helvetica-Bold", fontSize: 18);
    header.Color = Color.DarkBlue;

    var border = page.AddRectangle(
        x: 50, y: 50,
        width: 512, height: 692,
        fillColor: null,
        strokeColor: Color.Gray
    );
    border.StrokeWidth = 1;

    var content = page.AddText($"This is the content of page {i + 1}.", x: 100, y: 600, font: "Times-Roman", fontSize: 12);
    content.Color = Color.Black;

    var footer = page.AddText("Generated with PdfiumWrapper", x: 200, y: 30, font: "Helvetica", fontSize: 8);
    footer.Color = Color.Gray;

    page.GenerateContent();
}

document.Save("multipage.pdf");
```

### Example 4: Shapes and Graphics

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

var title = page.AddText("Shapes Demo", x: 250, y: 750, font: "Helvetica-Bold", fontSize: 20);
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

### Example 5: Invoice Layout

```csharp
using var document = new PdfDocument();
using var page = document.AddPage();

float pageWidth = 612;
float pageHeight = 792;
float margin = 50;

// Company header
var companyName = page.AddText("ACME Corporation", margin, pageHeight - 50, font: "Helvetica-Bold", fontSize: 24);
companyName.Color = Color.DarkBlue;

var tagline = page.AddText("Quality Products Since 1990", margin, pageHeight - 75, font: "Helvetica", fontSize: 10);
tagline.Color = Color.Gray;

// Invoice title and details
page.AddText("INVOICE", pageWidth - 150, pageHeight - 50, font: "Helvetica-Bold", fontSize: 28);
page.AddText("Invoice #: INV-2024-001", pageWidth - 200, pageHeight - 100, font: "Helvetica", fontSize: 10);
page.AddText("Date: January 15, 2024", pageWidth - 200, pageHeight - 115, font: "Helvetica", fontSize: 10);

// Horizontal rule
page.AddRectangle(margin, pageHeight - 140, pageWidth - 2 * margin, 1, Color.Gray, null);

// Bill To section
page.AddText("Bill To:", margin, pageHeight - 170, font: "Helvetica-Bold", fontSize: 12);
page.AddText("John Smith", margin, pageHeight - 190);
page.AddText("123 Main Street", margin, pageHeight - 205);
page.AddText("Anytown, ST 12345", margin, pageHeight - 220);

// Table header
page.AddRectangle(margin, pageHeight - 280, pageWidth - 2 * margin, 25, Color.LightGray, null);
page.AddText("Description", margin + 10, pageHeight - 270, font: "Helvetica-Bold", fontSize: 10);
page.AddText("Qty", 350, pageHeight - 270, font: "Helvetica-Bold", fontSize: 10);
page.AddText("Price", 420, pageHeight - 270, font: "Helvetica-Bold", fontSize: 10);
page.AddText("Total", 500, pageHeight - 270, font: "Helvetica-Bold", fontSize: 10);

// Table row
page.AddText("Widget Pro", margin + 10, pageHeight - 300);
page.AddText("5", 350, pageHeight - 300);
page.AddText("$99.99", 420, pageHeight - 300);
page.AddText("$499.95", 500, pageHeight - 300);

// Totals
page.AddText("Subtotal:", 420, pageHeight - 350);
page.AddText("$499.95", 500, pageHeight - 350);
page.AddText("Tax (8%):", 420, pageHeight - 370);
page.AddText("$40.00", 500, pageHeight - 370);
page.AddText("Total:", 420, pageHeight - 400, font: "Helvetica-Bold", fontSize: 14);
page.AddText("$539.95", 500, pageHeight - 400, font: "Helvetica-Bold", fontSize: 14);

// Footer
var thankYou = page.AddText("Thank you for your business!", margin, 80, font: "Helvetica-Oblique", fontSize: 12);
thankYou.Color = Color.Gray;

page.GenerateContent();
document.Save("invoice.pdf");
```

---

## Quick Reference

```csharp
// Create document and page
using var document = new PdfDocument();
using var page = document.AddPage(width: 612, height: 792);

// Text (standard fonts only)
var text = page.AddText("Hello", x: 100, y: 700, font: "Helvetica", fontSize: 12);
text.Color = Color.Black;

// Image (PNG or JPEG)
byte[] imageBytes = File.ReadAllBytes("logo.png");
var image = page.AddImage(imageBytes, x: 100, y: 500, width: 200, height: 100);

// Rectangle
var rect = page.AddRectangle(x: 100, y: 400, width: 200, height: 100,
    fillColor: Color.LightBlue, strokeColor: Color.Black);

// Path
var path = page.AddPath();
path.MoveTo(100, 300).LineTo(200, 300).LineTo(150, 200).Close();
path.FillColor = Color.Red;
path.StrokeColor = Color.Black;
path.SetDrawMode(PdfPathFillMode.Winding, stroke: true);

// Read objects
for (int i = 0; i < page.ObjectCount; i++)
{
    if (page.GetObject(i) is PdfImageObject img)
    {
        RawBitmap? pixels = img.GetBitmap();
    }
}

// Save
page.GenerateContent();  // Required!
document.Save("output.pdf");
```

---

## Notes

1. **Always call `GenerateContent()`** after adding, changing or removing page objects, before saving.

2. **Ownership of page objects.** An object on a page belongs to the page: disposing its wrapper does nothing to it, and the wrapper becomes unusable when the page is disposed. An object removed with `RemoveObject` belongs to you: dispose it, or the document destroys it when the document is disposed.

3. **Fonts.** Only the 14 standard fonts are available. Embedding other fonts is not supported.

4. **Images** are decoded with libjpeg-turbo and libpng; only PNG and JPEG are accepted.

5. **Coordinate system.** Y increases upward, and (0,0) is at the bottom-left.

6. **One object, one thread.** Do not use the same document, page or page object from two threads at once. See [Best Practices](BEST-PRACTICES.md#thread-safety).
