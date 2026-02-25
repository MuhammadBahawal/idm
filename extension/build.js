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

// inject.ts must use iife format since it runs in the page's MAIN world
const injectBuildOptions = {
    entryPoints: ['src/inject.ts'],
    outdir: 'dist',
    bundle: true,
    format: 'iife',
    target: 'chrome120',
    minify: !isWatch,
    sourcemap: false,
};

async function build() {
    if (isWatch) {
        const ctx = await esbuild.context(buildOptions);
        await ctx.watch();
        const ctx2 = await esbuild.context(injectBuildOptions);
        await ctx2.watch();
        console.log('Watching for changes...');
    } else {
        await esbuild.build(buildOptions);
        await esbuild.build(injectBuildOptions);
        console.log('Build complete.');
    }
}

build().catch(() => process.exit(1));
