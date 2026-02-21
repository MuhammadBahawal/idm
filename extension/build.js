const esbuild = require('esbuild');
const path = require('path');

const isWatch = process.argv.includes('--watch');

const buildOptions = {
    entryPoints: [
        'src/background.ts',
        'src/content.ts',
        'src/popup.ts',
    ],
    outdir: 'dist',
    bundle: true,
    format: 'esm',
    target: 'chrome120',
    minify: !isWatch,
    sourcemap: isWatch,
};

async function build() {
    if (isWatch) {
        const ctx = await esbuild.context(buildOptions);
        await ctx.watch();
        console.log('Watching for changes...');
    } else {
        await esbuild.build(buildOptions);
        console.log('Build complete.');
    }
}

build().catch(() => process.exit(1));
