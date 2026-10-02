"""Reapply recipe launcher changes after vendoring the pinned upstream."""
from pathlib import Path

root = Path(globals().get('TARGET_UPSTREAM', Path(__file__).resolve().parents[1] / 'dsh-pet/upstream'))

def patch(name, old, new):
    file = root / name
    text = file.read_text(encoding='utf8')
    assert text.count(old) == 1, (name, old[:60])
    file.write_text(text.replace(old, new), encoding='utf8')

patch('preload.js', "contextBridge.exposeInMainWorld('petBridge', {", """contextBridge.exposeInMainWorld('petBridge', {
  clickDelay: Number(process.env.TXAGENT_PET_CLICK_DELAY) || 500,
  openRecipes(anchor) { ipcRenderer.send('pet:open-recipes', anchor); },
  manageRecipes(anchor) { ipcRenderer.send('pet:manage-recipes', anchor); },""")
patch('sprite.js', "this.hit.addEventListener('click', () => this.onClick()", "this.hit.addEventListener('click', (e) => this.onRecipeClick(e)")
patch('sprite.js', '  dispose() {', '  dispose() {\n    clearTimeout(this._recipeClickTimer);')
patch('sprite.js', '  onPointerDown(e) {', """  recipeAnchor() {
    const r = this.hitRect;
    return {x:toScreen(this.pos.x+VIEW.x+r.x),y:toScreen(this.pos.y+VIEW.y+r.y),width:toScreen(r.w),height:toScreen(r.h)};
  }

  onRecipeClick(e) {
    clearTimeout(this._recipeClickTimer);
    if (this.justDragged || this.dragState.active || this.menuOpen || this.chatOpen || e.detail > 1) return;
    this.stopThrow();
    this.stopMove();
    this._recipeClickTimer = setTimeout(() => {
      if (!this.el.isConnected || this.justDragged || this.dragState.active || this.menuOpen || this.chatOpen) return;
      window.petBridge.openRecipes(this.recipeAnchor());
    }, window.petBridge.clickDelay);
  }

  onPointerDown(e) {""")
patch('sprite.js', '    if (e.button !== 0) return;', '    if (e.button !== 0) return;\n    clearTimeout(this._recipeClickTimer);')
patch('sprite.js', '  onContextMenu(e) {', '  onContextMenu(e) {\n    clearTimeout(this._recipeClickTimer);')
patch('sprite.js', "    const tools = [", "    const tools = [\n      { label: '快捷配方', action: 'quick-recipes' },\n      { label: '配方管理', action: 'manage-recipes' },")
patch('sprite.js', "    if (leaf.action === 'open-site') {", """    if (leaf.action === 'quick-recipes' || leaf.action === 'manage-recipes') {
      const method = leaf.action === 'quick-recipes' ? 'openRecipes' : 'manageRecipes';
      window.petBridge[method](this.recipeAnchor());
      return;
    }
    if (leaf.action === 'open-site') {""")
