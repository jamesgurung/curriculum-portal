const navMenu = document.querySelector('.nav-menu');

document.addEventListener('click', event => {
  if (!navMenu.open || navMenu.contains(event.target)) return;

  event.preventDefault();
  event.stopImmediatePropagation();
  navMenu.open = false;
}, true);
