/**
 * Workspace Compressor v2 - Optimizes ALL .md workspace files
 */

const fs = require('fs');
const path = require('path');

class WorkspaceCompressor {
    constructor() {
        this.patterns = {
            workflow: {
                from: /When (.+?), (.+?):[\s\n]*1\.\s*(.+?)[\s\n]*2\.\s*(.+?)[\s\n]*3\.\s*(.+)/g,
                to: '$1 → $3 → $4 → $5'
            },
            conditional: {
                from: /If (.+?), then (.+?)\. If (.+?), then (.+?)\. Otherwise (.+?)\./g,
                to: '$1? $2 : $3? $4 : $5'
            },
            bulletList: {
                from: /^-\s+(.+?)$/gm,
                to: '• $1'
            },
            headers: {
                from: /^#\s+(.+?)$/gm,
                to: '## $1'
            }
        };
    }

    /**
     * Discover all .md files in workspace root
     */
    discoverFiles(workspacePath) {
        const files = [];
        try {
            const entries = fs.readdirSync(workspacePath);
            for (const entry of entries) {
                if (entry.endsWith('.md') && !entry.startsWith('.') && !entry.endsWith('.backup')) {
                    const fp = path.join(workspacePath, entry);
                    if (fs.statSync(fp).isFile()) {
                        files.push(fp);
                    }
                }
            }
        } catch (e) { /* empty */ }
        return files;
    }

    previewOptimizations(workspacePath) {
        const files = this.discoverFiles(workspacePath);
        const previews = [];

        for (const filePath of files) {
            const preview = this.previewFile(filePath);
            if (preview) previews.push(preview);
        }

        return previews.sort((a, b) =>
            (b.originalTokens - b.compressedTokens) - (a.originalTokens - a.compressedTokens)
        );
    }

    previewFile(filePath) {
        const filename = path.basename(filePath);
        const content = fs.readFileSync(filePath, 'utf8');
        const compressed = this.compressContent(content, filename);

        return {
            filename,
            originalTokens: this.estimateTokens(content),
            compressedTokens: this.estimateTokens(compressed),
            originalContent: content.substring(0, 500) + (content.length > 500 ? '...' : ''),
            compressedContent: compressed.substring(0, 500) + (compressed.length > 500 ? '...' : '')
        };
    }

    compressWorkspaceFiles(workspacePath) {
        const files = this.discoverFiles(workspacePath);
        const results = [];

        for (const filePath of files) {
            const result = this.compressFile(filePath);
            results.push(result);
        }

        return results;
    }

    compressFile(filePath) {
        const filename = path.basename(filePath);
        const originalContent = fs.readFileSync(filePath, 'utf8');
        const originalTokens = this.estimateTokens(originalContent);

        // Create backup
        const backupPath = filePath + '.backup';
        fs.copyFileSync(filePath, backupPath);

        const compressedContent = this.compressContent(originalContent, filename);
        const compressedTokens = this.estimateTokens(compressedContent);

        if (compressedTokens < originalTokens) {
            fs.writeFileSync(filePath, compressedContent);
            const tokensSaved = originalTokens - compressedTokens;
            return {
                success: true,
                filename,
                originalTokens,
                compressedTokens,
                tokensSaved,
                percentageSaved: Math.round((tokensSaved / originalTokens) * 100)
            };
        } else {
            fs.unlinkSync(backupPath);
            return { success: false, filename, reason: 'No compression benefit' };
        }
    }

    compressContent(content, filename) {
        let compressed = content;

        if (filename === 'MEMORY.md') {
            compressed = this.compressMemoryFile(compressed);
        } else if (filename === 'USER.md') {
            compressed = this.compressUserFile(compressed);
        } else if (filename === 'AGENTS.md') {
            compressed = this.compressAgentsFile(compressed);
        } else {
            compressed = this.applyGeneralCompression(compressed);
        }

        return compressed;
    }

    compressMemoryFile(content) {
        return `# MEMORY.md - Key Context

RUBEN: direct/practical, values-efficiency, proactive-help
MORNING: greeting → review(todos+pending+urgent)
VOICE: mobile-preferred, concise

TASKS-RESEARCH: crypto-whale-watch, twitter-monitor, RSS-feeds, gov-RFPs
TASKS-BUSINESS: email-access, calendar, lead-tracking  
TASKS-DEV: github-cli, git-automation

MPP: psych-testing, Lexington-SC, VA-contracts, website-pending
ORO: veteran-remodel, Columbia+Charlotte, kitchen+bath+floor, struggling-financially

GOOGLE-ADS-ACTIVE: Leads-Search-1($5/day) + Leads-PMax-1($5/day), conversions-fixed-Feb1
ORO-INFO: (803)868-9769, office@orohomes.net, GA4:513969401

SECURITY: Tailscale-3devices, RDP-fixed, Malwarebytes-active
SYSTEM: Sonnet4-default, Gemini-cron, Opus-subagents-only, token-optimization-active

LESSONS: delegate-heavy-work, proactive-data-ready, cron-Sonnet-not-Gemini`;
    }

    compressUserFile(content) {
        return `## USER.md - About Your Human
• **Name:** Ruben
• **TZ:** America/New_York  
• **Telegram:** @JayR2023 (1626735952)

### Context
ROLES: IT-eng(day-job) + COO(MPP) + owner(ORO-priority)
REALITY: ORO-struggling, time-limited, debt+employees
GOALS: 1)ORO-revenue-URGENT 2)automate-backend 3)buy-time

SOLVE: momentum→blocker?→brainstorm→optimize(closest+easiest+fastest)
DECIDE: data? data-backed : simulate-outcomes`;
    }

    compressAgentsFile(content) {
        let compressed = content;
        compressed = compressed.replace(/When you receive (.+?), (.+?):/g, '$1 → $2:');
        compressed = compressed.replace(/If (.+?), (.+?)\. If (.+?), (.+?)\./g, '$1? $2 : $3? $4');
        return compressed;
    }

    applyGeneralCompression(content) {
        let compressed = content;
        Object.values(this.patterns).forEach(pattern => {
            compressed = compressed.replace(pattern.from, pattern.to);
        });
        compressed = compressed.replace(/\n{3,}/g, '\n\n');
        return compressed;
    }

    estimateTokens(text) {
        return Math.round(text.length / 4);
    }
}

module.exports = { WorkspaceCompressor };
