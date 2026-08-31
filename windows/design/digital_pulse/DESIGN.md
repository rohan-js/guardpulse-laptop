---
name: Digital Pulse
colors:
  surface: '#fcf8fb'
  surface-dim: '#dcd9dc'
  surface-bright: '#fcf8fb'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f6f3f5'
  surface-container: '#f0edef'
  surface-container-high: '#eae7ea'
  surface-container-highest: '#e4e2e4'
  on-surface: '#1b1b1d'
  on-surface-variant: '#414755'
  inverse-surface: '#303032'
  inverse-on-surface: '#f3f0f2'
  outline: '#717786'
  outline-variant: '#c1c6d7'
  surface-tint: '#005bc1'
  primary: '#0058bc'
  on-primary: '#ffffff'
  primary-container: '#0070eb'
  on-primary-container: '#fefcff'
  inverse-primary: '#adc6ff'
  secondary: '#006e28'
  on-secondary: '#ffffff'
  secondary-container: '#6ffb85'
  on-secondary-container: '#00732a'
  tertiary: '#bc000a'
  on-tertiary: '#ffffff'
  tertiary-container: '#e2241f'
  on-tertiary-container: '#fffbff'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#d8e2ff'
  primary-fixed-dim: '#adc6ff'
  on-primary-fixed: '#001a41'
  on-primary-fixed-variant: '#004493'
  secondary-fixed: '#72fe88'
  secondary-fixed-dim: '#53e16f'
  on-secondary-fixed: '#002107'
  on-secondary-fixed-variant: '#00531c'
  tertiary-fixed: '#ffdad5'
  tertiary-fixed-dim: '#ffb4aa'
  on-tertiary-fixed: '#410001'
  on-tertiary-fixed-variant: '#930005'
  background: '#fcf8fb'
  on-background: '#1b1b1d'
  surface-variant: '#e4e2e4'
  glass-surface: rgba(255, 255, 255, 0.75)
  glass-border: rgba(255, 255, 255, 0.15)
  digital-blue-tint: '#E5F1FF'
  surface-background: '#F2F2F7'
typography:
  display-hero:
    fontFamily: Hanken Grotesk
    fontSize: 72px
    fontWeight: '700'
    lineHeight: 80px
    letterSpacing: -0.03em
  headline-lg:
    fontFamily: Hanken Grotesk
    fontSize: 32px
    fontWeight: '600'
    lineHeight: 40px
    letterSpacing: -0.01em
  headline-md:
    fontFamily: Hanken Grotesk
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.01em
  body-lg:
    fontFamily: Hanken Grotesk
    fontSize: 18px
    fontWeight: '400'
    lineHeight: 28px
    letterSpacing: 0em
  body-md:
    fontFamily: Hanken Grotesk
    fontSize: 15px
    fontWeight: '400'
    lineHeight: 22px
    letterSpacing: 0.01em
  label-md:
    fontFamily: Hanken Grotesk
    fontSize: 13px
    fontWeight: '600'
    lineHeight: 18px
    letterSpacing: 0.04em
  label-sm:
    fontFamily: Hanken Grotesk
    fontSize: 11px
    fontWeight: '500'
    lineHeight: 16px
    letterSpacing: 0.06em
  headline-lg-mobile:
    fontFamily: Hanken Grotesk
    fontSize: 28px
    fontWeight: '600'
    lineHeight: 36px
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  unit: 4px
  gutter-sm: 16px
  gutter-lg: 24px
  margin-page: 40px
  stack-xs: 4px
  stack-sm: 12px
  stack-md: 20px
  stack-lg: 32px
  stack-xl: 48px
---

## Brand & Style

The design system embodies a "Modern Guardian" persona—combining the surgical precision of enterprise security with a fluid, accessible consumer interface. It moves away from heavy, static blocks toward a lighter, more ethereal aesthetic that feels integrated into the host operating system while maintaining a distinct, premium identity.

The chosen style is **Sophisticated Glassmorphism**, heavily influenced by the "Mica" and "Acrylic" principles of high-end desktop environments. The interface focuses on depth, using light-refractive surfaces and multi-layered shadows to establish a clear spatial hierarchy. The emotional goal is to evoke **serene control**: the user should feel that their environment is secure and orderly, yet open and modern.

## Colors

The palette is centered around **Digital Blue**, a vibrant and high-energy hue that signifies active protection and technical sophistication. 

- **Primary (Digital Blue):** The heartbeat of the system. Used for critical actions, active progress, and primary branding.
- **Secondary (Vibrant Mint):** Represents "Safe" states, active uptime, and successful authentication.
- **Tertiary (System Red):** Used with extreme intentionality for "Access Denied" states, critical alerts, and time-exhaustion warnings.
- **Neutral:** A deep, professional slate used for typography and icons to ensure high contrast against translucent surfaces.

Backgrounds leverage semi-transparent whites and light grays to facilitate the glass effect. All "glass" components should feature a subtle 1px inner border using `glass-border` to simulate light catching the edge of a physical lens.

## Typography

This design system utilizes **Hanken Grotesk** for its sharp, contemporary geometry and exceptional readability at small sizes. The tracking is intentionally tightened for headlines to create a "locked-in" professional feel, while body and label text utilize increased tracking to enhance legibility on blurred backgrounds.

Hierarchy is enforced through weight variation rather than just size. Headlines use a semi-bold weight (600) to stand out against glass surfaces, while labels use a medium weight (500) with slight letter spacing for a technical, metadata-driven appearance.

## Layout & Spacing

The layout philosophy is **Airy & Centered**. We utilize a 12-column fluid grid for desktop application windows, but critical interactions (like PIN entry or lock screens) follow a **Focused Container** model, where content is restricted to a 480px or 640px central column.

- **Grid:** 12-column with 24px gutters.
- **Margins:** Generous 40px outer margins to prevent the UI from feeling cramped against the screen edges.
- **Rhythm:** An 8px base unit drives the system, but we utilize 4px increments for micro-adjustments within components. 
- **Adaptation:** On mobile/compact views, margins scale down to 16px and the 12-column grid collapses to a single-column layout with 16px horizontal padding.

## Elevation & Depth

Depth is the primary navigator of the interface. We utilize three distinct levels of elevation:

1.  **Base Layer (Mica):** The primary window surface. Uses a 40px backdrop blur and a `glass-surface` fill. It features a 1px white inner stroke at 15% opacity.
2.  **Floating Cards (Acrylic):** Secondary containers that hold groups of settings. These use a slightly more opaque blur (20px) and a multi-layered shadow: a 2px sharp shadow for definition and a 12px soft ambient shadow for lift.
3.  **Active Overlays (Level 3):** Modals and PIN pads. These use a 32px diffused shadow with a 5% tint of the primary color to create a "glow" effect, separating the action clearly from the background.

Avoid solid black shadows; shadows should always be soft, diffused, and tinted by the background color.

## Shapes

The shape language is inspired by the **Windows 11 "Rounded"** aesthetic—professional yet soft. 

- **Interactive Elements:** Buttons and input fields use a consistent 8px (`0.5rem`) radius.
- **Content Containers:** Cards and grouping containers use a 16px (`1rem`) radius.
- **Primary Windows:** The main app frame uses a 24px (`1.5rem`) radius to feel approachable and modern.
- **Status Indicators:** Use pill-shaped (fully rounded) geometry for status chips and notification badges to distinguish them from functional UI buttons.

## Components

### Buttons
- **Primary:** Solid "Digital Blue" with white text. Apply a subtle top-down gradient (light to dark) for a tactile feel.
- **Glass / Secondary:** Semi-transparent white with a 1px border. On hover, the opacity increases.
- **Ghost:** No fill, Digital Blue text. Used for less frequent secondary actions.

### Numeric Keypad
Buttons should be 64px x 64px with a 12px radius. Use the "Glass" style with a 1px inner border. The font should be `headline-md`.

### Cards
Cards are the primary organizational unit. They should always feature a 1px inner border (`glass-border`) and the Level 2 shadow defined in the Elevation section. Padding within cards should be a minimum of 24px (`stack-lg`).

### Input Fields
Fields use a subtle grey-wash fill (`rgba(0,0,0,0.05)`) with a bottom-only 2px border in Digital Blue that animates to full width on focus.

### PIN Progress
Instead of flat circles, use small glass spheres. Unfilled states are 1px outlines; filled states are glowing Digital Blue pulses.

### Status Chips
Pill-shaped containers with a 10% opacity fill of the status color (Green/Red/Blue) and a solid text label in that same color. This ensures they are readable but not visually heavy.