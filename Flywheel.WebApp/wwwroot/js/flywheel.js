// flywheel.js - Minimal Limitless behaviors for the current layout
// Requires Bootstrap bundle (window.bootstrap) to be loaded first.

(function () {
  'use strict';

  function qsa(sel, root) { return Array.from((root || document).querySelectorAll(sel)); }
  function qs(sel, root) { return (root || document).querySelector(sel); }

  function sidebarMainResize() {
    const sidebarMain = qs('.sidebar-main');
    if (!sidebarMain) return;

    const togglers = qsa('.sidebar-main-resize');
    const resizeClass = 'sidebar-main-resized';
    const unfoldClass = 'sidebar-main-unfold';
    const unfoldDelay = 150;
    let tStart, tFinish;

    togglers.forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        sidebarMain.classList.toggle(resizeClass);
        if (!sidebarMain.classList.contains(resizeClass)) {
          sidebarMain.classList.remove(unfoldClass);
        }
      });
    });

    sidebarMain.addEventListener('mouseenter', () => {
      clearTimeout(tFinish);
      tStart = setTimeout(() => {
        if (sidebarMain.classList.contains(resizeClass)) sidebarMain.classList.add(unfoldClass);
      }, unfoldDelay);
    });

    sidebarMain.addEventListener('mouseleave', () => {
      clearTimeout(tStart);
      tFinish = setTimeout(() => sidebarMain.classList.remove(unfoldClass), unfoldDelay);
    });
  }

  function sidebarMobileToggle(sidebarSelector, togglerSelector) {
    const sidebar = qs(sidebarSelector);
    if (!sidebar) return;

    const togglers = qsa(togglerSelector);
    const expandedClass = 'sidebar-mobile-expanded';

    togglers.forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        sidebar.classList.toggle(expandedClass);

        // close other sidebars when opening one
        qsa('.sidebar:not(' + sidebarSelector + '):not(.sidebar-component)').forEach(s => {
          s.classList.remove(expandedClass);
        });
      });
    });
  }

  function sidebarEndToggle() {
    // Desktop + mobile toggles for right sidebar
    const sidebarEnd = qs('.sidebar-end');
    if (!sidebarEnd) return;

    const desktop = qsa('.sidebar-end-toggle');
    const collapsedClass = 'sidebar-collapsed';

    desktop.forEach(btn => {
      btn.addEventListener('click', (e) => {
        // If it's a mobile toggler, let mobile handler run (it also has sidebar-mobile-end-toggle class)
        if (btn.classList.contains('sidebar-mobile-end-toggle')) return;
        e.preventDefault();
        sidebarEnd.classList.toggle(collapsedClass);
      });
    });

    sidebarMobileToggle('.sidebar-end', '.sidebar-mobile-end-toggle');
  }

  function navigationSidebarAccordion() {
    // Implements Limitless-like accordion for .nav-sidebar .nav-item-submenu
    const navs = qsa('.nav-sidebar:not(.nav-scrollspy)');
    if (!navs.length) return;

    const openClass = 'nav-item-open';

    navs.forEach(nav => {
      const links = qsa('.nav-item-submenu > .nav-link:not(.disabled)', nav);
      links.forEach(link => {
        link.addEventListener('click', (e) => {
          e.preventDefault();
          const container = link.closest('.nav-item-submenu');
          const submenu = container ? container.querySelector(':scope > .nav-group-sub') : null;
          if (!container || !submenu || !window.bootstrap) return;

          const isOpen = container.classList.contains(openClass);

          // Toggle current
          const bsCollapse = window.bootstrap.Collapse.getOrCreateInstance(submenu, { toggle: false });
          if (isOpen) {
            bsCollapse.hide();
            container.classList.remove(openClass);
          } else {
            bsCollapse.show();
            container.classList.add(openClass);
          }

          // Accordion behavior
          if (nav.getAttribute('data-nav-type') === 'accordion') {
            Array.from(container.parentNode.children).forEach(sibling => {
              if (sibling !== container && sibling.classList && sibling.classList.contains(openClass)) {
                sibling.classList.remove(openClass);
                qsa(':scope > .nav-group-sub', sibling).forEach(sm => {
                  const sibCollapse = window.bootstrap.Collapse.getOrCreateInstance(sm, { toggle: false });
                  sibCollapse.hide();
                });
              }
            });
          }
        });
      });
    });
  }

  function init() {
    sidebarMainResize();

    // Main sidebar mobile toggle
    sidebarMobileToggle('.sidebar-main', '.sidebar-mobile-main-toggle');

    // Right sidebar desktop + mobile
    sidebarEndToggle();

    // Nav accordion
    navigationSidebarAccordion();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
