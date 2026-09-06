#!/usr/bin/env node

/**
 * Token Saver - Dashboard
 * 
 * Two sections:
 * 1. Workspace Files - Scans ALL .md files, shows "possible savings" until applied
 * 2. Model Audit - Detects current models, suggests cheaper alternatives
 */

const fs = require('fs');
const path = require('path');
const { TokenAnalyzer } = require('./analyzer.js');
const { WorkspaceCompressor } = require('./compressor.js');

class TokenOptimizerV2 {
    constructor() {
        this.analyzer = new TokenAnalyzer();
        this.compressor = new WorkspaceCompressor();
    }

    async run(command = 'dashboard', args = []) {
        const workspacePath = this.findWorkspace();

        switch (command) {
            case 'dashboard': return this.showDashboard(workspacePath);
            case 'tokens': return this.optimizeTokens(workspacePath, args);
            case 'models': return this.showModelAudit(workspacePath);
            case 'revert': return this.revertChanges(args[0], workspacePath);
            default: return this.showDashboard(workspacePath);
        }
    }

    findWorkspace() {
        let dir = process.cwd();
        if (dir.includes('skills' + path.sep + 'token-optimizer')) {
            return path.resolve(dir, '..', '..');
        }
        return dir;
    }

    async showDashboard(workspacePath) {
        const analysis = await this.analyzer.analyzeWorkspace(workspacePath);
        const previews = this.compressor.previewOptimizations(workspacePath);
        const modelAudit = this.analyzer.auditModels(workspacePath);

        const fileSavings = this.calculatePossibleSavings(previews);

        // Check if any optimizations have been applied (backups exist)
        const hasBackups = this.findBackups(workspacePath).length > 0;
        const savingsLabel = hasBackups ? 'Actual savings' : 'Possible saving';

        console.log(`🚀 **Token Saver Dashboard**

💾 **Current Context:** ${analysis.totalTokens.toLocaleString()} tokens across ${analysis.fileList.length} files
💰 **Est. Monthly Cost:** $${(analysis.monthlyCostEstimate || 0).toFixed(2)}

╭─────────────────────────────────────────────────────────╮
│  🗜️  WORKSPACE FILES OPTIMIZATION                       │
╰─────────────────────────────────────────────────────────╯
**All .md files in workspace context (${previews.length} files found):**
${this.formatFileList(previews, hasBackups)}

**${savingsLabel}:** ${fileSavings.tokens.toLocaleString()} tokens (${fileSavings.percentage}% possible saving)
**Monthly:** ~$${fileSavings.monthlyCost.toFixed(2)}/month possible saving

➤ Run: \`/optimize tokens\` to apply compression

╭─────────────────────────────────────────────────────────╮
│  🤖  AI MODEL AUDIT                                     │
╰─────────────────────────────────────────────────────────╯
**Current model usage:**
${this.formatCurrentModels(modelAudit.current)}

**Optimization suggestions:**
${this.formatModelSuggestions(modelAudit.suggestions)}

**Total possible model savings:** ~$${modelAudit.totalPossibleSavings.toFixed(2)}/month

➤ Run: \`/optimize models\` for detailed model analysis

╭─────────────────────────────────────────────────────────╮
│  📊 COMBINED POSSIBLE SAVINGS                           │
╰─────────────────────────────────────────────────────────╯
**File compression:** ~$${fileSavings.monthlyCost.toFixed(2)}/month possible saving
**Model switching:** ~$${modelAudit.totalPossibleSavings.toFixed(2)}/month possible saving
**Total:** ~$${(fileSavings.monthlyCost + modelAudit.totalPossibleSavings).toFixed(2)}/month possible saving

💡 These are estimates until changes are applied. Use \`/optimize revert\` to undo file changes.`);
    }

    async showModelAudit(workspacePath) {
        const modelAudit = this.analyzer.auditModels(workspacePath);

        console.log(`🤖 **AI Model Audit - Detailed Analysis**

╭─────────────────────────────────────────────────────────╮
│  CURRENT MODEL CONFIGURATION                             │
╰─────────────────────────────────────────────────────────╯
${this.formatCurrentModelsDetailed(modelAudit.current)}

╭─────────────────────────────────────────────────────────╮
│  RECOMMENDED CHANGES                                     │
╰─────────────────────────────────────────────────────────╯
${this.formatModelSuggestionsDetailed(modelAudit.suggestions)}

╭─────────────────────────────────────────────────────────╮
│  AVAILABLE MODELS & PRICING                              │
╰─────────────────────────────────────────────────────────╯
${this.formatAvailableModels()}

💡 Model changes require updating OpenClaw gateway config.
   These are possible savings — actual savings depend on usage patterns.`);
    }

    calculatePossibleSavings(filePreviews) {
        const totalBefore = filePreviews.reduce((sum, p) => sum + p.originalTokens, 0);
        const totalAfter = filePreviews.reduce((sum, p) => sum + p.compressedTokens, 0);
        const saved = totalBefore - totalAfter;
        const percentage = totalBefore > 0 ? Math.round((saved / totalBefore) * 100) : 0;
        const monthlyCost = (saved * 0.003 * 4.33);

        return { tokens: saved, percentage, monthlyCost };
    }

    formatFileList(filePreviews, hasBackups) {
        if (filePreviews.length === 0) return '  (no .md files found)';

        return filePreviews.map(preview => {
            const savings = preview.originalTokens - preview.compressedTokens;
            const percentage = preview.originalTokens > 0
                ? Math.round((savings / preview.originalTokens) * 100) : 0;
            const status = percentage > 75 ? '🔴' : percentage > 40 ? '🟡' : '🟢';
            const label = hasBackups ? 'saved' : 'possible saving';
            return `${status} **${preview.filename}:** ${preview.originalTokens.toLocaleString()} → ${preview.compressedTokens.toLocaleString()} tokens (${percentage}% ${label})`;
        }).join('\n');
    }

    formatCurrentModels(models) {
        const lines = [];
        for (const [role, info] of Object.entries(models)) {
            if (info.model === 'unknown') {
                lines.push(`• **${this.roleLabel(role)}:** ⚠️ Not detected`);
            } else {
                const cost = info.estimatedMonthlyCost > 0
                    ? ` (~$${info.estimatedMonthlyCost.toFixed(2)}/month)`
                    : ' (free)';
                lines.push(`• **${this.roleLabel(role)}:** ${info.model}${cost}`);
            }
        }
        return lines.join('\n');
    }

    formatCurrentModelsDetailed(models) {
        const lines = [];
        for (const [role, info] of Object.entries(models)) {
            lines.push(`**${this.roleLabel(role)}**`);
            lines.push(`  Model: ${info.model || 'unknown'}`);
            lines.push(`  Detected from: ${info.detectedFrom || 'not found'}`);
            lines.push(`  Est. cost: $${(info.estimatedMonthlyCost || 0).toFixed(2)}/month`);
            lines.push('');
        }
        return lines.join('\n');
    }

    formatModelSuggestions(suggestions) {
        if (suggestions.length === 0) {
            return '  ✅ No obvious model optimizations detected\n  (Run `/optimize models` for full analysis)';
        }

        return suggestions.map(s => {
            const saving = s.monthlySaving > 0
                ? `~$${s.monthlySaving.toFixed(2)}/month possible saving`
                : 'minimal saving';
            return `💡 **${s.role}:** Switch ${s.current} → ${s.suggested} — ${saving}`;
        }).join('\n');
    }

    formatModelSuggestionsDetailed(suggestions) {
        if (suggestions.length === 0) {
            return '✅ Current model configuration looks cost-efficient!\n';
        }

        return suggestions.map((s, i) => {
            let detail = `**${i + 1}. ${s.role}: ${s.current} → ${s.suggested}**
   Reason: ${s.reason}
   Current cost: ~$${s.currentMonthlyCost.toFixed(2)}/month
   New cost: ~$${s.newMonthlyCost.toFixed(2)}/month
   **Possible saving: ~$${s.monthlySaving.toFixed(2)}/month**
   Confidence: ${s.confidence}`;
            if (s.note) detail += `\n   ⚠️ Note: ${s.note}`;
            return detail;
        }).join('\n\n');
    }

    formatAvailableModels() {
        const { MODEL_PRICING } = require('./analyzer.js');
        const lines = [];

        const tiers = { free: [], budget: [], standard: [], premium: [] };
        for (const [key, info] of Object.entries(MODEL_PRICING)) {
            tiers[info.tier].push({ key, ...info });
        }

        if (tiers.free.length) {
            lines.push('**🟢 Free Tier:**');
            tiers.free.forEach(m => lines.push(`  • ${m.label} — $0 (great for cron/background tasks)`));
        }
        if (tiers.budget.length) {
            lines.push('**🟡 Budget:**');
            tiers.budget.forEach(m => lines.push(`  • ${m.label} — $${m.input}/1K input tokens`));
        }
        if (tiers.standard.length) {
            lines.push('**🟠 Standard:**');
            tiers.standard.forEach(m => lines.push(`  • ${m.label} — $${m.input}/1K input tokens`));
        }
        if (tiers.premium.length) {
            lines.push('**🔴 Premium:**');
            tiers.premium.forEach(m => lines.push(`  • ${m.label} — $${m.input}/1K input tokens`));
        }

        return lines.join('\n');
    }

    roleLabel(role) {
        const labels = {
            default: 'Default (main chat)',
            cron: 'Cron jobs',
            subagent: 'Subagents'
        };
        return labels[role] || role;
    }

    async optimizeTokens(workspacePath, args = []) {
        console.log('🗜️  **Optimizing All Workspace .md Files...**\n');

        const results = this.compressor.compressWorkspaceFiles(workspacePath);
        let totalSaved = 0;
        let filesChanged = 0;

        console.log('**Results:**');
        results.forEach(result => {
            if (result.success && result.tokensSaved > 0) {
                console.log(`✅ ${result.filename}: ${result.originalTokens.toLocaleString()} → ${result.compressedTokens.toLocaleString()} tokens (${result.percentageSaved}% saved)`);
                totalSaved += result.tokensSaved;
                filesChanged++;
            } else if (!result.success) {
                console.log(`⏭️  ${result.filename}: ${result.reason}`);
            }
        });

        // Enable persistent AI-efficient writing mode
        const persistentEnabled = this.enablePersistentMode(workspacePath);

        const monthlySavings = (totalSaved * 0.003 * 4.33);

        console.log(`\n💾 **Optimization Complete!**
• **Files optimized:** ${filesChanged}/${results.length}
• **Tokens saved:** ${totalSaved.toLocaleString()}
• **Actual savings:** ~$${monthlySavings.toFixed(2)}/month
${persistentEnabled ? '• **✨ Persistent Mode: ON** — AI will maintain compressed format going forward' : '• **Persistent Mode:** Already enabled'}

🛡️  Backups created (.backup extension)
➤ \`/optimize revert\` to restore originals + turn off persistent mode`);
    }

    /**
     * Enable persistent AI-efficient writing mode by adding instruction to AGENTS.md
     * This ensures the AI continues writing in compressed notation after optimization
     */
    enablePersistentMode(workspacePath) {
        const agentsPath = path.join(workspacePath, 'AGENTS.md');
        if (!fs.existsSync(agentsPath)) return false;

        const content = fs.readFileSync(agentsPath, 'utf8');
        const marker = '## 📝 Token Saver — Persistent Mode';
        
        // Already enabled
        if (content.includes(marker)) return false;

        const instruction = `
${marker}
**Status: ENABLED** — Turn off with \`/optimize revert\`

When writing to workspace .md files (MEMORY.md, USER.md, TOOLS.md, SOUL.md, etc.),
always use AI-efficient notation:
- Dense key:value format over verbose paragraphs
- Symbols over words (→, +, |, &)  
- Abbreviations over full phrases
- One-liners over multi-line explanations
- Preserve all meaning, minimize all tokens

**Example:** Instead of writing "Ruben prefers to receive brief morning updates 
with a review of tasks and any urgent items", write:
\`MORNING: greeting → review(todos+pending+urgent)\`

This keeps workspace files lean so they cost less on every API call.
`;

        // Backup AGENTS.md before modifying
        const backupPath = agentsPath + '.backup';
        if (!fs.existsSync(backupPath)) {
            fs.copyFileSync(agentsPath, backupPath);
        }

        fs.appendFileSync(agentsPath, instruction);
        return true;
    }

    revertChanges(target, workspacePath) {
        const backups = this.findBackups(workspacePath);

        if (backups.length === 0) {
            console.log('📁 **No backups found** — nothing to revert.');
            return;
        }

        let toRevert = target && target !== 'all'
            ? backups.filter(b => b.includes(target))
            : backups;

        if (toRevert.length === 0) {
            console.log(`❌ **No backups found for:** ${target}`);
            return;
        }

        console.log('🔄 **Reverting Files...**\n');

        toRevert.forEach(backupPath => {
            const originalPath = backupPath.replace('.backup', '');
            fs.copyFileSync(backupPath, originalPath);
            fs.unlinkSync(backupPath);
            console.log(`✅ Restored: ${path.basename(originalPath)}`);
        });

        // Also remove persistent mode from AGENTS.md if it was added
        this.disablePersistentMode(workspacePath);

        console.log(`\n✅ **Revert Complete!** ${toRevert.length} file(s) restored.
• **Persistent Mode: OFF** — AI will write normally again`);
    }

    /**
     * Remove persistent mode instruction from AGENTS.md
     */
    disablePersistentMode(workspacePath) {
        const agentsPath = path.join(workspacePath, 'AGENTS.md');
        if (!fs.existsSync(agentsPath)) return;

        const content = fs.readFileSync(agentsPath, 'utf8');
        const marker = '## 📝 Token Saver — Persistent Mode';
        const markerIndex = content.indexOf(marker);
        
        if (markerIndex === -1) return;

        // Remove everything from the marker to the end of the persistent mode section
        const before = content.substring(0, markerIndex).trimEnd();
        fs.writeFileSync(agentsPath, before + '\n');
    }

    findBackups(workspacePath) {
        try {
            return fs.readdirSync(workspacePath)
                .filter(file => file.endsWith('.backup'))
                .map(file => path.join(workspacePath, file));
        } catch (e) {
            return [];
        }
    }
}

// CLI
if (require.main === module) {
    const optimizer = new TokenOptimizerV2();
    const command = process.argv[2] || 'dashboard';
    const args = process.argv.slice(3);
    optimizer.run(command, args).catch(console.error);
}

module.exports = { TokenOptimizerV2 };
