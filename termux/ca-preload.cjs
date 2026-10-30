'use strict';
/*
 * PocketIris runtime shims for the official Yep Anywhere on Android.
 *
 * Loaded with `node --require` (see ca-start), before Yep starts, so that Yep's own files stay exactly as the
 * official release made them. It fixes two things that only go wrong on Android:
 *
 *   1. Android's app storage refuses hard links (fs.link fails with EACCES, EPERM, ENOSYS, ENOTSUP or EXDEV).
 *      Yep uses a hard link to publish a file without ever overwriting an existing one. Copying with the "exclusive"
 *      flag has the same effect (it fails if the target exists), so that is what is done when a hard link is refused.
 *
 *   2. The native password library "bcrypt" cannot be built here. Its pure-JavaScript twin "bcryptjs" has the same
 *      functions, so every `import ... from "bcrypt"` is pointed at bcryptjs.
 *
 * If a shim cannot be set up it says so on stderr and does nothing, so Yep then behaves as the official release does.
 */

const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');
const { pathToFileURL } = require('node:url');

/** The error codes that mean "this storage does not allow hard links". */
const REFUSED = new Set(['EACCES', 'EPERM', 'ENOSYS', 'ENOTSUP', 'EXDEV']);
const EXCL = fs.constants.COPYFILE_EXCL;

//  Shim 1a: the promise version of link (used by `import { link } from "node:fs/promises"`).
const originalPromisesLink = fsp.link;
fsp.link = async function link(existingPath, newPath) {
  try {
    return await originalPromisesLink.call(this, existingPath, newPath);
  } catch (error) {
    //  Any other problem (for example "the target already exists") is passed on unchanged.
    if (!error || !REFUSED.has(error.code)) throw error;
    return fsp.copyFile(existingPath, newPath, EXCL);
  }
};

//  Shim 1b: the callback version.
const originalLink = fs.link;
fs.link = function link(existingPath, newPath, callback) {
  originalLink.call(fs, existingPath, newPath, (error) => {
    //  Fall back to an exclusive copy when hard links are refused.
    if (error && REFUSED.has(error.code)) return fs.copyFile(existingPath, newPath, EXCL, callback);
    callback(error);
  });
};

//  Shim 1c: the synchronous version.
const originalLinkSync = fs.linkSync;
fs.linkSync = function linkSync(existingPath, newPath) {
  try {
    return originalLinkSync.call(fs, existingPath, newPath);
  } catch (error) {
    if (!error || !REFUSED.has(error.code)) throw error;
    return fs.copyFileSync(existingPath, newPath, EXCL);
  }
};

//  Make the changed functions visible to modules that import them with `import { link } from "node:fs/promises"`.
Module.syncBuiltinESMExports();

//  Shim 2: send "bcrypt" to "bcryptjs".
try {
  const wanted = require.resolve('bcryptjs', {
    paths: [process.cwd(), path.join(os.homedir(), 'ya-android'), __dirname],
  });
  const target = pathToFileURL(wanted).href;

  //  Without module hooks (Node older than 22.15) this shim cannot work, so say so.
  if (typeof Module.registerHooks !== 'function') throw new Error('this Node has no module.registerHooks');
  Module.registerHooks({
    resolve(specifier, context, nextResolve) {
      //  Only the exact name "bcrypt" is redirected; everything else resolves as usual.
      if (specifier === 'bcrypt') return nextResolve(target, context);
      return nextResolve(specifier, context);
    },
  });
} catch (error) {
  process.stderr.write('[ca-preload] bcrypt shim not active: ' + error.message + '\n');
}
