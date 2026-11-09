# testPj 微信小程序

这是一个使用微信小程序原生语法的基础工程，可直接用微信开发者工具打开。

## 开始使用

1. 安装并打开微信开发者工具。
2. 选择“导入项目”，将项目目录选为本仓库根目录。
3. `project.config.json` 中的 `appid` 默认使用体验 AppID `touristappid`。如需真机预览或上传，请替换为你的小程序 AppID。
4. 导入后即可编译和预览首页。

## 目录结构

```text
├── app.js                 小程序入口
├── app.json               全局页面与窗口配置
├── app.wxss               全局样式
├── project.config.json    微信开发者工具项目配置
├── sitemap.json           页面索引规则
└── pages/
    └── index/             首页
        ├── index.js
        ├── index.json
        ├── index.wxml
        └── index.wxss
```

新增页面时，在 `app.json` 的 `pages` 中注册页面路径，并按需添加对应的页面文件。
