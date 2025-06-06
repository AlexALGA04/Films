﻿using Films.Context;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using static Films.Services.TmbdService;
using Films.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Films.Models;
using Films.Models.APIModels;

namespace Films.Controllers
{
    public class MoviesController : Controller
    {
        private readonly FilmsDbContext _context;

        private readonly TmdbService _tmdbService;

        public MoviesController(FilmsDbContext context, TmdbService tmdbService)
        {
            _tmdbService = tmdbService;
            _context = context;
        }

        [Route("movie/{id}/detail")] // Ruta personalizada para la acción Detail cuando se hace clic en una movie-card
        public async Task<IActionResult> Detail(int id)
        {
            var movie = await _tmdbService.GetMovieById(id);

            if (movie == null)
                return NotFound();

            var userIdClaim = User.FindFirst("UserId");
            int idUser = userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;

            var userLists = await _context.Lists
                .Where(l => l.FkIdUser == idUser)
                .ToListAsync();

            // Recuperar la nota de review promedio de esta peli
            var review = _context.MovieReviews.FirstOrDefault(r => r.FkIdMovie == movie.Id);
            movie.Review = review?.AverageRating ?? 0;

            var genreIds = movie.Genres.Select(g => g.Id).ToList();
            int genreIdToUse = genreIds.FirstOrDefault();

            var relatedMovies = (await _tmdbService.GetMoviesByGenreAsync(genreIdToUse))
                .Where(m => m.Id != movie.Id)
                .Take(30) // limitar la cantidad
                .ToList();

            // Obtener la lista de actores
            var actors = await _tmdbService.GetPopularActorsByMovieId(movie.Id);
            movie.Persons = actors;

            // Recuperar las reviews
            var reviews = await _context.Reviews
                .Include(r => r.FkIdUserNavigation) // Incluye el usuario que escribió la review
                .Where(r => r.FkIdMovie == movie.Id)
                .OrderByDescending(r => r.IdReview)
                .ToListAsync();

            var reviewUserIds = reviews.Select(r => r.FkIdUser).Distinct().ToList();

            var userStates = await _context.Lists
                .Where(l => l.FkIdMovie == movie.Id && reviewUserIds.Contains(l.FkIdUser))
                .ToDictionaryAsync(l => l.FkIdUser, l => l.FkIdTypeList);

            var movieReview = await _context.MovieReviews
                .FirstOrDefaultAsync(mr => mr.FkIdMovie == movie.Id);


            var vm = new MovieDetailsViewModel
            {
                Id = movie.Id,
                Title = movie.Title,
                Genres = movie.Genres,
                Rating = movieReview?.AverageRating ?? 0, // la "nota"  de la película
                Reviews = reviews, // En plural, la lista de todos los comentarios de la peli
                Overview = movie.Overview,
                UserMovieLists = userLists,
                PosterPath = movie.PosterPath,
                BackdropPath = movie.BackdropPath,
                ReleaseDate = DateTime.Parse(movie.ReleaseDate),
                RelatedMovies = relatedMovies,
                Persons = movie.Persons,
                ReviewUserStates = userStates
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddReview(string titleReview, string descriptionReview, int ratingInput, int idFilm)
        {
            if (!string.IsNullOrWhiteSpace(titleReview) && !string.IsNullOrWhiteSpace(descriptionReview) && ratingInput > 0)
            {
                var userIdClaim = User.FindFirst("UserId");
                int idUser = userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;

                // ¿Ya existe una review de este usuario para esta peli?
                var existingReview = _context.Reviews
                    .FirstOrDefault(r => r.FkIdUser == idUser && r.FkIdMovie == idFilm);

                if (existingReview == null)
                {
                    // Crear nueva review
                    var newReview = new Review
                    {
                        FkIdMovie = idFilm,
                        Rating = ratingInput,
                        Title = titleReview,
                        Description = descriptionReview,
                        FkIdUser = idUser
                    };

                    _context.Reviews.Add(newReview);
                }
                else
                {
                    // Editar review existente
                    existingReview.Title = titleReview;
                    existingReview.Description = descriptionReview;
                    existingReview.Rating = ratingInput;

                    _context.Reviews.Update(existingReview);
                }

                _context.SaveChanges();

                // Recalcular la media
                var ratings = _context.Reviews
                    .Where(r => r.FkIdMovie == idFilm)
                    .Select(r => r.Rating)
                    .ToList();

                decimal average = (decimal)ratings.Sum() / ratings.Count;

                var movieReviewEntry = _context.MovieReviews
                    .FirstOrDefault(mr => mr.FkIdMovie == idFilm);

                if (movieReviewEntry == null)
                {
                    _context.MovieReviews.Add(new MovieReview
                    {
                        FkIdMovie = idFilm,
                        AverageRating = average
                    });
                }
                else
                {
                    movieReviewEntry.AverageRating = average;
                    _context.MovieReviews.Update(movieReviewEntry);
                }

                _context.SaveChanges();
            }

            return RedirectToAction("Detail", "Movies", new { id = idFilm });
        }

        public async Task<IActionResult> ViewList(int id, int? userId)
        {
            int idUser;
            bool isMyProfile = true;

            // Determinar de quién es el perfil
            if (userId.HasValue)
            {
                idUser = userId.Value;

                var currentId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");
                isMyProfile = (idUser == currentId);
            }
            else
            {
                var userIdClaim = User.FindFirst("UserId");
                idUser = userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;
            }

            // Obtener IDs de películas de la lista
            var movieIds = await _context.Lists
                .Where(l => l.FkIdUser == idUser && l.FkIdTypeList == id)
                .Select(l => l.FkIdMovie)
                .ToListAsync();

            // Obtener detalles desde el servicio TMDb
            var movies = new List<Movie>();
            foreach (var movieId in movieIds)
            {
                try
                {
                    var movie = await _tmdbService.GetMovieById(movieId);
                    if (movie != null)
                        movies.Add(movie);
                }
                catch (HttpRequestException)
                {
                    // Ignorar errores (ej. 404 si la peli ya no existe)
                    continue;
                }
            }

            // Obtener el tipo de lista
            var listType = await _context.TypeLists.FirstOrDefaultAsync(t => t.IdListType == id);

            // Pasar datos a la vista
            ViewBag.AllLists = await _context.TypeLists.ToListAsync();
            ViewBag.IsMyProfile = isMyProfile;
            ViewBag.UserId = idUser;

            var vm = new UserListViewModel
            {
                Movies = movies,
                ListName = listType?.ListName ?? "Lista",
            };

            return View(vm);
        }


    }
}
