# Devir notu — soru havuzu (2026-11-11)

**Amaç:** Quiz oyununun soru havuzunu genişletmek ve derinleştirmek. Oyun kodu bu oturumun işi değildi; sadece havuzun çalışması için gereken üç değişiklik yazıldı.
**Durum:** `main` @ fbfe1d4, v0.5.0 etiketli ve imaj GHCR'a basıldı (release run 2026-11-10 16:03, success). PR #1 ve #2 merge edildi, `claude/question-pool-depth` dalında açık iş kalmadı. `personal-website` içindeki pin v0.5.0 görünüyor ama **sunucuya uygulandığını doğrulamadım**.

**Yapılanlar:**
- 3 kategori → 21 kategori × 10 soru (210, PR #1) → 11 kategori × 40 soru (440, PR #2). Hiçbir soru kaybedilmedi, 21'in 11'e eşleşmesi PR #2 açıklamasında.
- Tur artık setin tamamını değil havuzdan rastgele 10 soru oynuyor (`Room.Start` → `Draw`), loader "tam 10" yerine "en az 10" istiyor.
- Fact-check sonrası 17 soru düzeltildi: 4 soru metni, 13 çeldirici. Doğru şık hiç değişmedi.
- Her dosyada dört suit tam 10'ar kez doğru; 440 soru metni birbirinden farklı; en uzun şık 27 karakter.
- Testler 244 geçiyor. Çekiliş ve puanlama yolları mutasyonla doğrulandı (sette okuma, indeks ilerletmeme).

**Açık işler:**
1. `cg-11` "Türkiye kaç ilden oluşur" — 19 yeni il teklifi (Kasım 2026, geçmedi) geçerse cevap bayatlar.
2. Turlar arası "aynı soru tekrar gelmesin" takibi yok; oda ömrünü aşan state gerektirdiği için kapsam dışı bırakıldı.
3. Havuz 40'ın üstüne çıkarılacaksa dosya başına suit dengesi elle korunmalı; bunu zorlayan test yok, sadece "10'ar kez" iddiası var.

**Dikkat:**
- PR'lara koşan CI **yok** (`release.yml` sadece `v*` etiketinde), yani "yeşil" elle koşulmuş demek; sorular da imaja gömülü, merge tek başına sunucuya hiçbir şey göndermez — etiket + pin bump gerekir.
- Telefonda metin yok, sorular TV'den okunuyor: şıklar kısa ve tek doğru cevaplı olmalı.
- En sık hata eş anlamlı çeldirici ("öğüt/nasihat", "temiz/titiz"), ikincisi soruyu kökten tartışmaya açan ifade ("en büyük canlı" → mantar).
- TDK sorgusu `sozluk.gov.tr/gts?ara=<kelime>`; site JS kabuğu, atasözleri her maddenin `atasozu` dizisinde. TDK her atasözünü içermiyor.
- Depo herkese açık: hiçbir soruda işveren ya da şirkete özgü içerik olmayacak.

**İlgili dosyalar:** `src/Quiz/questions/*.json` (11 dosya), `src/Quiz/Game/JsonQuestionSetSource.cs`, `src/Quiz/Game/Room.cs`, `tests/Quiz.Tests/JsonQuestionSetSourceTests.cs`, `tests/Quiz.Tests/RoomTests.cs`, `tests/Quiz.Tests/GameHubTests.cs`
