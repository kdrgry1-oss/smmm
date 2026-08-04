# gib-robot — GİB portal robotu (Node 20 + Playwright chromium)
#
# Taban imaj Playwright'ın resmi imajıdır: chromium ve tüm sistem bağımlılıkları
# hazır gelir (ayrıca `npx playwright install` gerekmez). Sürüm, package.json'daki
# playwright sürümüyle AYNI olmalıdır — uyuşmazlık "browser not found" hatası verir.
FROM mcr.microsoft.com/playwright:v1.47.2-jammy

ENV NODE_ENV=production \
    PORT=8080 \
    SESSION_DIR=/data/sessions \
    SHOT_DIR=/tmp

WORKDIR /app

# Bağımlılıklar önce kurulur (katman önbelleği).
COPY package.json package-lock.json* ./
# Lockfile varsa yeniden üretilebilir kurulum (npm ci); yoksa npm install'a düşülür.
RUN if [ -f package-lock.json ]; then npm ci --omit=dev; else npm install --omit=dev; fi

COPY . .

# Oturum önbelleği dizini (compose'da `gib-sessions` volume'u buraya bağlanır).
RUN mkdir -p /data/sessions && chown -R pwuser:pwuser /data /app

# NON-ROOT: Playwright imajındaki hazır kullanıcı.
USER pwuser

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD node -e "require('http').get('http://127.0.0.1:8080/health',r=>process.exit(r.statusCode===200?0:1)).on('error',()=>process.exit(1))"

CMD ["node", "server.js"]
