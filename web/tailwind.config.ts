import type { Config } from "tailwindcss";

const config: Config = {
  content: [
    "./app/**/*.{ts,tsx}",
    "./components/**/*.{ts,tsx}",
    "./lib/**/*.{ts,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        navy: {
          50: "#f0f4fa",
          100: "#dce5f2",
          200: "#bccbe4",
          300: "#8fa8ce",
          400: "#5c80b3",
          500: "#3b6098",
          600: "#2c4b7e",
          700: "#253d66",
          800: "#1e3253",
          900: "#152642",
          950: "#0b1830",
        },
      },
    },
  },
  plugins: [],
};

export default config;
