<script setup lang="ts">
/**
 * Decorative backdrop of the sign-in pages: a Gaulish landscape (hills, standing stones, a village of round huts
 * behind a palisade) under a sky, a girl on the front hill flying the logo's winged letter as a kite, and a soft
 * golden glow behind the form.
 * Day in light mode, a starry night in dark mode. Purely decorative: hidden from screen readers, no pointer events.
 */

const WIDTH = 1440
const HEIGHT = 900

/** Round huts with thatched conical roofs; `x` is the center, `base` the ground line. */
const huts = [
  { x: 150, base: 812, w: 74, h: 30, roof: 62 },
  { x: 262, base: 806, w: 96, h: 36, roof: 78 },
  { x: 384, base: 812, w: 70, h: 28, roof: 58 },
].map(({ x, base, w, h, roof }) => ({
  wall: `M${x - w / 2} ${base} V${base - h} H${x + w / 2} V${base}Z`,
  roof: `M${x - w / 2 - 12} ${base - h + 6} Q${x - w / 4} ${base - h - roof * 0.45} ${x} ${base - h - roof} Q${x + w / 4} ${base - h - roof * 0.45} ${x + w / 2 + 12} ${base - h + 6}Z`,
  door: `M${x - 9} ${base} V${base - 14} A9 9 0 0 1 ${x + 9} ${base - 14} V${base}Z`,
}))

/** Pointed stakes of the palisade in front of the huts, with a gap for the gate. */
const palisade = Array.from({ length: 30 }, (_, i) => 70 + i * 13)
  .filter((x) => x < 236 || x > 274)
  .map((x, i) => {
    const top = 818 - (i % 3) * 3
    return `M${x} 846 V${top} L${x + 5} ${top - 9} L${x + 10} ${top} V846Z`
  })
  .join(' ')

/** Standing stones on the hill to the right. */
const menhirs = [
  'M1040 726 C1036 700 1040 662 1050 640 C1056 628 1068 626 1072 640 C1080 666 1082 700 1078 726Z',
  'M1094 726 C1092 708 1096 688 1104 680 C1110 676 1118 682 1120 694 C1123 706 1122 716 1120 726Z',
  'M1136 728 C1135 716 1138 704 1144 700 C1150 698 1156 704 1157 714 C1158 720 1157 724 1156 728Z',
]

/** Oaks: a trunk and a crown of overlapping circles. */
const trees = [
  { x: 548, base: 818, r: 30 },
  { x: 1296, base: 806, r: 36 },
  { x: 1352, base: 812, r: 26 },
].map(({ x, base, r }) => ({
  trunk: `M${x - 4} ${base} V${base - r * 1.4} H${x + 4} V${base}Z`,
  crown: [
    [x, base - r * 2.1, r],
    [x - r * 0.7, base - r * 1.6, r * 0.75],
    [x + r * 0.7, base - r * 1.6, r * 0.75],
  ],
}))

/** The kite: the logo's winged letter, its line down to the girl's hand and a tail of little bows. */
const KITE = { x: 1130, y: 215, size: 132, tilt: -9 }
const KITE_H = (KITE.size * 22) / 28
/** Where the line meets the kite (below the letter) and the girl's raised hand. */
const anchor = { x: KITE.x - 4, y: KITE.y + KITE_H * 0.42 }
/** The girl is drawn at a base size and scaled around her feet. */
const GIRL_SCALE = 1.2
const FEET = { x: 880, y: 834 }
const handAt = { x: 905, y: 745 }
const hand = { x: FEET.x + (handAt.x - FEET.x) * GIRL_SCALE, y: FEET.y + (handAt.y - FEET.y) * GIRL_SCALE }
const kiteLine = `M${hand.x} ${hand.y} Q${hand.x + 150} ${hand.y - 150} ${anchor.x} ${anchor.y}`
const tail = `M${anchor.x} ${anchor.y} C${anchor.x - 22} ${anchor.y + 40} ${anchor.x + 26} ${anchor.y + 70} ${anchor.x + 4} ${anchor.y + 112} S${anchor.x - 30} ${anchor.y + 170} ${anchor.x - 6} ${anchor.y + 205}`
/** Little bows along the tail. */
const bows = [0.28, 0.55, 0.82].map((t) => ({
  x: anchor.x + Math.sin(t * Math.PI * 2.2) * 14 - 2,
  y: anchor.y + t * 205,
}))

/** The girl (before scaling about 100 units tall), standing on the front hill, pigtail braids for a Gaulish touch. */
const girl = {
  head: { cx: 880, cy: 751, r: 11.5 },
  braids: 'M869 749 C861 752 856 759 855 768 C855 772 859 773 860 769 C861 763 865 758 871 756Z M891 749 C899 752 904 759 905 768 C905 772 901 773 900 769 C899 763 895 758 889 756Z',
  ribbons: [
    [857, 770],
    [903, 770],
  ],
  dress: 'M871 766 C874 764 886 764 889 766 L893 790 C896 798 899 804 902 809 L858 809 C861 804 864 798 867 790Z',
  legs: 'M872 808 H877 V830 H872Z M883 808 H888 V830 H883Z',
  shoes: 'M869 829 H879 C880 834 868 834 869 829Z M881 829 H891 C892 834 880 834 881 829Z',
  arms: `M887 770 L${handAt.x} ${handAt.y} M873 770 L862 791`,
}

/** A fixed sprinkle of stars (only visible at night). */
const stars = Array.from({ length: 70 }, (_, i) => {
  // Deterministic pseudo-random positions, so the sky looks the same on every visit.
  const a = Math.sin(i * 12.9898) * 43758.5453
  const b = Math.sin(i * 78.233) * 12345.6789
  return { x: (a - Math.floor(a)) * WIDTH, y: (b - Math.floor(b)) * 560, r: 0.6 + ((i * 7) % 5) * 0.25 }
})

const wing =
  'M9.3 20.45C8.6 20 7.5 19 6.9 18.4C6.3 17.8 5.9 17.2 5.9 16.8C5.8 16.2 6.6 15.6 7.1 15.3C6.5 14.9 5.9 14.4 5.85 13.9C5.7 13.3 6.3 12.6 6.7 12.3C6.1 11.9 5.5 11.2 5.4 9.9C5.45 9.6 5.8 9.4 6 9.25C5.4 8 4.6 6.4 4.6 5.5C4.55 4.6 4.8 4 5.05 4C5.3 3.9 5.5 4.3 5.6 5C5.8 6.6 6 7.7 6.45 8.7C7 10 7.8 11.9 8.1 12.6C8.6 13.6 9.4 15 9.7 15.8C10.1 16.7 10.2 17.6 10.15 18.3C10.1 19.1 9.8 20.2 9.3 20.45Z'
</script>

<template>
  <div class="backdrop auth-backdrop pointer-events-none absolute inset-0 -z-10 overflow-hidden" aria-hidden="true">
    <svg :viewBox="`0 0 ${WIDTH} ${HEIGHT}`" preserveAspectRatio="xMidYMax slice" class="size-full">
      <defs>
        <linearGradient id="auth-sky" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" class="sky-top" />
          <stop offset="1" class="sky-bottom" />
        </linearGradient>
        <radialGradient id="auth-glow" cx="0.5" cy="0.5" r="0.5">
          <stop offset="0" class="glow-center" />
          <stop offset="1" class="glow-edge" />
        </radialGradient>
        <!-- The logo's winged letter, without its tile. -->
        <symbol id="auth-letter" viewBox="2 2 28 22">
          <g transform="translate(0.8 0) rotate(-8 9.6 20.2)"><path class="wing" :d="wing" /></g>
          <g transform="matrix(-1 0 0 1 32 0)">
            <g transform="translate(0.8 0) rotate(-8 9.6 20.2)"><path class="wing" :d="wing" /></g>
          </g>
          <rect class="letter" x="10.4" y="15.2" width="11.2" height="8.4" rx="1.6" />
          <path class="flap" d="M11.2 16.1 Q13.4 18.6 16 19.7 Q18.6 18.6 20.8 16.1" />
        </symbol>
      </defs>

      <rect :width="WIDTH" :height="HEIGHT" fill="url(#auth-sky)" />
      <g class="stars">
        <circle v-for="(star, i) in stars" :key="i" :cx="star.x" :cy="star.y" :r="star.r" />
      </g>
      <ellipse cx="720" cy="430" rx="560" ry="380" fill="url(#auth-glow)" />


      <!-- Landscape, back to front. -->
      <path class="hill-far" d="M0 690 C180 640 320 650 480 672 C640 694 760 640 920 628 C1080 616 1240 650 1440 630 V900 H0Z" />
      <g transform="translate(0 -30)">
        <path v-for="(d, i) in menhirs" :key="`m${i}`" class="stone" :d="d" />
      </g>
      <path class="hill-mid" d="M0 760 C160 720 300 712 460 736 C620 760 720 742 860 716 C1000 690 1180 704 1440 734 V900 H0Z" />
      <g v-for="(tree, i) in trees" :key="`t${i}`" class="tree">
        <path :d="tree.trunk" />
        <circle v-for="(c, j) in tree.crown" :key="j" :cx="c[0]" :cy="c[1]" :r="c[2]" />
      </g>
      <g v-for="(hut, i) in huts" :key="`h${i}`">
        <path class="hut-wall" :d="hut.wall" />
        <path class="hut-roof" :d="hut.roof" />
        <path class="hut-door" :d="hut.door" />
      </g>
      <path class="palisade" :d="palisade" />
      <path class="hill-near" d="M0 836 C200 818 380 812 560 824 C760 838 980 828 1180 814 C1300 806 1380 810 1440 816 V900 H0Z" />

      <!-- The girl and her kite: the line runs from her raised hand to the winged letter. -->
      <path class="kite-line" :d="kiteLine" />
      <g class="kite" :style="{ transformOrigin: `${anchor.x}px ${anchor.y}px` }">
        <path class="kite-tail" :d="tail" />
        <g v-for="(bow, i) in bows" :key="`b${i}`" :transform="`translate(${bow.x} ${bow.y})`">
          <path class="bow" d="M0 0 L-9 -6 L-9 6Z M0 0 L9 -6 L9 6Z" />
        </g>
        <use
          href="#auth-letter"
          :x="KITE.x - KITE.size / 2"
          :y="KITE.y - KITE_H / 2"
          :width="KITE.size"
          :height="KITE_H"
          :transform="`rotate(${KITE.tilt} ${KITE.x} ${KITE.y})`"
        />
      </g>
      <g class="girl" :transform="`translate(${FEET.x} ${FEET.y}) scale(${GIRL_SCALE}) translate(${-FEET.x} ${-FEET.y})`">
        <path :d="girl.braids" />
        <circle :cx="girl.head.cx" :cy="girl.head.cy" :r="girl.head.r" />
        <path :d="girl.legs" />
        <path :d="girl.shoes" />
        <path :d="girl.dress" />
        <path class="girl-arms" :d="girl.arms" />
        <circle v-for="(r, i) in girl.ribbons" :key="`r${i}`" class="bow" :cx="r[0]" :cy="r[1]" r="2.6" />
      </g>
    </svg>
  </div>
</template>

<style>
/* Theme colors as variables; unscoped because a scoped style cannot target the .dark class on <html>. */
.auth-backdrop {
  --sky-top: #ffffff;
  --sky-bottom: #f8f3e6;
  --glow: #eda100;
  --glow-strength: 0.32;
  --hill-far: #f0ece2;
  --hill-mid: #e7e2d6;
  --hill-near: #ddd7c9;
  --stone: #d6d0c1;
  --tree: #dcd6c8;
  --hut-wall: #e2dccf;
  --hut-roof: #d3ccbb;
  --hut-door: #c4bcaa;
  --palisade: #d1c9b8;
  --letter: #ffffff;
  --letter-edge: #c9c9c9;
  --line: #a39b88;
  --girl: #a69c85;
  --stars: 0;
}

.dark .auth-backdrop {
  --sky-top: #070707;
  --sky-bottom: #141414;
  --glow-strength: 0.16;
  --hill-far: #191919;
  --hill-mid: #1f1f1f;
  --hill-near: #262626;
  --stone: #2c2c2c;
  --tree: #232323;
  --hut-wall: #242424;
  --hut-roof: #2c2c2c;
  --hut-door: #1a1a1a;
  --palisade: #2b2b2b;
  --letter: #fafafa;
  --letter-edge: #fafafa;
  --line: #6b6b6b;
  --girl: #4f4f4f;
  --stars: 1;
}
</style>

<style scoped>
.sky-top {
  stop-color: var(--sky-top);
}
.sky-bottom {
  stop-color: var(--sky-bottom);
}
.glow-center {
  stop-color: var(--glow);
  stop-opacity: var(--glow-strength);
}
.glow-edge {
  stop-color: var(--glow);
  stop-opacity: 0;
}
.stars {
  fill: #ffffff;
  opacity: calc(var(--stars) * 0.55);
}
.hill-far {
  fill: var(--hill-far);
}
.hill-mid {
  fill: var(--hill-mid);
}
.hill-near {
  fill: var(--hill-near);
}
.stone {
  fill: var(--stone);
}
.tree {
  fill: var(--tree);
}
.hut-wall {
  fill: var(--hut-wall);
}
.hut-roof {
  fill: var(--hut-roof);
}
.hut-door {
  fill: var(--hut-door);
}
.palisade {
  fill: var(--palisade);
}
.kite-line {
  fill: none;
  stroke: var(--line);
  stroke-width: 1.4;
}
.kite-tail {
  fill: none;
  stroke: #eda100;
  stroke-width: 2;
  stroke-linecap: round;
}
.bow {
  fill: #eda100;
}
.girl {
  fill: var(--girl);
}
.girl-arms {
  fill: none;
  stroke: var(--girl);
  stroke-width: 5;
  stroke-linecap: round;
}
.wing {
  fill: #eda100;
}
.letter {
  fill: var(--letter);
  stroke: var(--letter-edge);
  stroke-width: 0.6;
}
.flap {
  fill: none;
  stroke: #171717;
  stroke-width: 1.35;
  stroke-linecap: round;
  stroke-linejoin: round;
}

/* The kite sways in the wind around the point where the line is tied. */
.kite {
  animation: sway 6s ease-in-out infinite;
}
@keyframes sway {
  0%,
  100% {
    transform: rotate(-3deg) translateY(0);
  }
  50% {
    transform: rotate(3deg) translateY(-8px);
  }
}
@media (prefers-reduced-motion: reduce) {
  .kite {
    animation: none;
  }
}
</style>
