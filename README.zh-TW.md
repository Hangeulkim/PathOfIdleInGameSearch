# Path of Idle 遊戲內搜尋

[English](README.md) · [한국어](README.ko.md) · [简体中文](README.zh-CN.md) · **繁體中文**

非官方 Windows 模組，在遊戲內搜尋物品、搭配裝備與技能、批次開箱、管理倉庫及強化。不需要瀏覽器儀表板或獨立伺服器。

**[下載最新版](https://github.com/Hangeulkim/PathOfIdleInGameSearch/releases/latest)** · [完整指南與範例（英文）](GUIDE.md)

獨立儲存庫為 **Hangeulkim/PathOfIdleInGameSearch**。**1.1.9** 包含 Healer 等持有跨職業 Alien 技能角色的自動裝備修正。先前 `path-of-idle-stats` 連結是發布錯誤，請使用本儲存庫。

## 安裝與更新

1. 下載並完整解壓縮 `PathOfIdleInGameSearch-v1.1.9.zip`。
2. 關閉遊戲，執行 `install.bat`。
3. 啟動遊戲，按 **F3** 或 **Ctrl+F**。

安裝程式驗證 SHA-256 並保留既有設定。只有缺少 BepInEx 時才下載並驗證固定的官方 BepInEx 6 IL2CPP x64。已有 BepInEx 的使用者也可把單獨下載的 DLL 放入 `BepInEx/plugins`。

## 功能與範例

- 依名稱、品質、部位、等級、詞綴或套裝搜尋背包、一般倉庫與 Vault。查看完整描述並依遊戲規則移動物品。`critical damage -health` 要求前兩個詞並排除 health；`|` 表示或，引號表示片語。
- Auto Build 同時比較八個裝備欄、技能與點數；可選技能轉換花費正常遊戲資源。已學 Alien 技能與裝備賦予技能分開處理。
- 主題：Auto、Physical、Elemental、Fire、Ice、Lightning、Minion、Bleed、Corrosion、Critical、Support、Defense。優先度 0 偏向一般表現，100 偏向所選主題，不強制指定物品。
- 火焰流選 Fire 並提高優先度；召喚流選 Minion，依原生標籤、歸屬、攻擊類型與繼承屬性判斷 Ally、Sentry、Totem；生存流選 Defense，在相同攻擊下比較減傷、閃避、護盾、恢復與存活時間。
- Ignore forge bonuses 僅在比較時使用強化前核心數值，真實強化、詞綴、符文與套裝效果保留。
- 批次開裝備/符文箱；Keep Vault gear, sell rest 保留進入 Vault 的獎勵，立即出售確認的溢出物品。更好獎勵替換舊物品時，被擠出的舊物品才出售。
- 一般倉庫整批出售獨立確認，排除 Vault、背包與已裝備物品。
- Forge 比較下一級收益或最大強化已裝備物品。神話強化批次檢查合格未裝備傳說物品，不需選擇角色，每次檢查容量、資源並回報成功、失敗及略過數。
- 面板可拖動/調整大小，支援四種語言、0.1×–100× 輸入與按需顯示紀錄。背景啟動與全域快捷鍵預設關閉。

## 對照畫面操作

以下為 v1.1.9 實際遊戲截圖，儲存的角色、數量、速度與選項只是範例，不是推薦預設值。點擊圖片可放大。

### 搜尋既有物品

在 **Item Search** 輸入文字，選擇是否包含 Warehouse/Vault，再用結果上方的兩個分頁切換位置。搜尋本身不會出售或裝備物品。

![v1.1.9 搜尋框與倉庫結果](docs/images/v1.1.9-search.jpg)

*頂部是 `minion` 文字查詢，不是 Auto Build 主題。中間按鈕篩選品質；每項右側的 **Move to inventory** 只移動該物品。滑鼠停留可查看完整效果。*

### 選擇自動搭配目標並確認

先核對藍框中的角色，再選主題與優先度。圖中是 Defense；100% 和不限時是此存檔的選擇，**Default 60** 可恢復預設搜尋時間。

![v1.1.9 角色、主題與優先度](docs/images/v1.1.9-auto-build-theme.jpg)

向下捲動，檢查倉庫範圍、**Ignore forge bonuses** 和付費技能轉換選項，再使用 **Auto-build Gear + Skills**。第一次點擊準備，5 秒內再次確認才開始計算並可能套用，後面沒有額外批准畫面。

![v1.1.9 自動搭配選項與執行按鈕](docs/images/v1.1.9-auto-build.jpg)

*寬按鈕是裝備與技能一起處理；下方兩個 Advanced 按鈕只處理其中一側。截圖時沒有執行自動搭配。*

### 開箱與倉庫出售

在目的地列選 **Inventory / Warehouse / Vault + Sell**，下方選擇箱子品質，再用顯示數量的裝備或符文開箱按鈕。

![v1.1.9 開箱目的地與出售按鈕](docs/images/v1.1.9-bulk-open.jpg)

***Vault + Sell** 保留進入 Vault 的物品並出售確認的溢出。紅色 **Sell all Warehouse gear (Vault excluded)** 是單獨清理一般倉庫，不是開箱按鈕。已完成的出售不能撤銷。*

### Forge 與神話強化

Forge 上方兩個按鈕分別用於升一級和最大強化已裝備物品；下方紫色按鈕先檢查未裝備傳說的神話候選。

![v1.1.9 Forge 與 Mythic 檢查](docs/images/v1.1.9-forge.jpg)

***Check all Mythic candidates (no consumption)** 第一次僅檢查，12 秒內確認同一批候選才進行真實付費嘗試；失敗會銷毀傳說物品，且不需要選擇角色。*

## 計算與安全

自動化由你選擇角色、設定主題並確認後啟動，在遊戲內本地讀取資料及呼叫正常遊戲函數，不是需要 AI 訂閱或 API 金鑰的雲端代理。主按鈕第一次點擊準備，5 秒內再次點擊才開始計算及實際套用；計算完成後沒有額外的批准畫面。技能轉換預設開啟、預設最多 12 次正常付費嘗試，可在執行前關閉。取消只在計算階段可用。

先依相容性、等級及效果結構縮小候選，再比較完整裝備與技能計畫，不是只憑稀有度或物品名稱裝備。主題 100 不代表強制每個技能都是該元素。30% 效果若有 10 次機會，在計算中計為期望 3 次，不操縱真實戰鬥機率。失敗時嘗試恢復裝備、技能及本次花費，但從 Vault 取出的候選可能保留在一般倉庫。

**舊裝備注意：** Auto Build 成功後的整理會嘗試把替換下來的物品存入 Vault，未保留在其中的可售物品可能被出售，並非保留所有舊裝備的模式。完整步驟見[英文說明](README.md#auto-build-from-your-click-to-the-equipped-result)。

搜尋時間 10 秒至不限，預設 60 秒；候選比較的模擬戰鬥固定為 30 秒，套用及驗證可能額外耗時。包含支援的技能時序、生命/魔力消耗、恢復、召喚、實際 2/4 件套、既有符文/符文之語；機率效果使用期望觸發次數，不自動重排散裝符文。

這不是所有敵人下的精確 DPS，也不保證全域最優，新機制可能需要更新。套用失敗會嘗試恢復並驗證原狀態。[測試說明](scripts/HEALER-REGRESSION.md)區分目前存檔檢查與歷史精確結果，不代表所有未知存檔均已驗證。

**自動化前請備份存檔。出售與神話強化失敗造成的銷毀不可復原。** 移除時關閉遊戲並執行 `uninstall.bat`，保留設定及其他模組。非官方粉絲專案，與遊戲開發者及 BepInEx 無關。
